# Deploy Runbook — post-2026-04-21 changes

One-page operator guide for deploying the Phase 9 `profile_ids` JWT claim
migration, the Next.js API-proxy rewrites, and the global snake_case JSON
policy. Read this before the first push to any environment above local
dev.

## Required environment variables

### muallimi-main-backend

| Variable | Required in | Notes |
|---|---|---|
| `IDENTITY_JWT_SECRET_KEY` | All non-dev | ≥32 bytes. Dev fallback (`dev-only-please-rotate-minimum-32-chars-secret-key`) is public in source; startup guard **throws** if it's in use outside `ASPNETCORE_ENVIRONMENT=Development`. |
| `IDENTITY_JWT_SECRET_KEY_PREVIOUS` | Only during rotation | ≥32 bytes. When set, the previous signing key is accepted by the validator alongside the current — enables zero-downtime rotation. See "JWT signing-key rotation" below. |
| `IDENTITY_JWT_ISSUER` | Recommended | Defaults to `muallimi-main-backend`. Change only if rotating issuers. |
| `IDENTITY_JWT_AUDIENCE` | Recommended | Defaults to `muallimi-platform`. |
| `IDENTITY_TOTP_ENCRYPTION_KEY` | All non-dev | Base64 AES-256 key (`openssl rand -base64 32`). Dev fallback is 32 zero bytes; startup guard **throws** if unset outside Development. |
| `IDENTITY_CORS_ALLOWED_ORIGINS` | All | Comma-separated list of frontend origins. |
| `REDIS_CONNECTION_STRING` | Recommended | Falls back to in-memory rate limiting + session cache when absent. |

### Muaallimi-Platform (frontend)

| Variable | Purpose | Per-env value |
|---|---|---|
| `NEXT_PUBLIC_API_BASE_URL` | Browser-side base URL for direct fetches (auth service). The browser resolves this. | Host-reachable URL of main-backend (e.g. `https://api.muallimi.app`). |
| `NEXT_PUBLIC_AUTH_API_URL` | Legacy alias used by `auth.service.ts`. Set to the same value as `NEXT_PUBLIC_API_BASE_URL`. | Same as above. |
| `BACKEND_INTERNAL_URL` | **Server-side** target for Next.js `rewrites()` — forwards `/api/*` proxy calls from the Next.js container to main-backend. | Internal-network URL: Docker service name in compose, K8s Service DNS in-cluster, private ALB hostname on ECS, etc. NEVER the public URL — adds a wasteful round-trip through the CDN. |

Deployment topologies for `BACKEND_INTERNAL_URL`:

| Topology | Example value |
|---|---|
| Docker Compose | `http://main-backend:5063` |
| Kubernetes (same namespace) | `http://muallimi-main-backend:5063` |
| Kubernetes (cross-namespace) | `http://main-backend.<ns>.svc.cluster.local:5063` |
| ECS / App Services | Internal load-balancer hostname for the backend service |

## New backend URL prefixes

`Muaallimi-Platform/next.config.js` proxies **specific prefixes only** from
the browser to `BACKEND_INTERNAL_URL`. If you ship a new backend URL
prefix that the frontend calls relatively (e.g. `/api/widgets/...`), you
MUST add it to the `beforeFiles` list in `next.config.js` or the browser
will 404 on the Next.js server. Today's list:

```
/api/auth /api/student /api/parent /api/teacher /api/school-admin
/api/operator /api/curriculum-admin /api/ai-ops /api/billing
/api/compliance /api/observability /api/notifications /api/at-risk
```

## Data migrations

### `20260421102519_BackfillManagedStudentProfiles`

Inserts one `student_profiles` row per managed child user (role =
`student`) that does not already have one. Idempotent — the
`NOT EXISTS` clause makes it safe to re-run.

Context: children created before `UserManagementService.CreateChildAsync`
started writing the profile row synchronously would otherwise see every
student surface (home, progress, leaderboard, study, tutor, mock-test,
homework-help, whiteboard) fail with `missing_identity`.

Apply as part of the standard migration CLI on first boot of the new
backend image:

```bash
docker exec <main-backend-container> dotnet Muallimi.Api.dll migrate
```

No separate operator step required; the migration is in the pipeline.

## Expected one-time traffic pattern on backend deploy

When the new backend image is promoted, every user with an active
session holds an access token from the previous build — without the new
`profile_ids` claim. On their next page load, the frontend's
`auth-context` detects the missing claim and silently calls
`POST /api/auth/refresh` exactly once per user. Expected impact:

- Brief spike on `/api/auth/refresh` within the first 15 minutes of
  rollout (at most one call per active session).
- Bounded by the refresh-token rotation + reuse-detection logic — no
  auth loops possible.
- No user action required. No forced re-login.

Watch your auth-refresh rate metric during the rollout window; expect it
to return to baseline after 15 minutes.

## JSON wire format

Global default for main-backend minimal APIs: **snake_case**
(`System.Text.Json.JsonNamingPolicy.SnakeCaseLower`). Identity/Auth DTOs
pin camelCase via explicit `[JsonPropertyName]` attributes — verified by
the `IdentityDtoJsonAttributesTests` contract test in CI.

Siblings that consume main-backend responses:
- `muallimi-ai-service`: no direct HTTP calls to main-backend. Safe.
- `muallimi-document-ingestion`: all main-backend HTTP calls configured
  with `JsonNamingPolicy.SnakeCaseLower`. Safe (fixed 2026-04-21).

When adding a new .NET service that calls main-backend: configure your
`JsonSerializerOptions` with `PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower`
on every `JsonSerializer.Deserialize` / `ReadFromJsonAsync` call site.
`PropertyNameCaseInsensitive = true` alone does NOT bridge PascalCase
to snake_case.

## JWT authentication pipeline (shipped 2026-04-21)

`AddAuthentication().AddJwtBearer()` is wired in
`IdentityServiceCollectionExtensions.AddIdentityModule`.
`UseAuthentication()` and `UseAuthorization()` run in `Program.cs`
before `UseIdentityTenantResolution()`, so `context.User` is populated
for every downstream middleware.

### Single source of truth for validation parameters

`JwtTokenServiceOptions.CreateValidationParameters()` is the one
builder used by **both** the bearer middleware and
`JwtTokenService.ValidateAccessToken`. Any change to validation
semantics (clock skew, key set, algorithm list, claim mapping) happens
in one place and both paths inherit — they cannot drift. Guarded by
the `JwtHardeningContractTests` suite.

### Guarantees

- `MapInboundClaims = false` on the bearer handler preserves the
  original JWT claim names (`sub`, `tenant_id`, `session_id`, `roles`,
  `profile_ids`, `impersonating`). Do NOT flip this to true — every
  reader in the repo assumes short names.
- All four `TokenValidationParameters.Validate*` flags are on.
- **Algorithm is pinned to HS256** via `ValidAlgorithms`. Tokens signed
  with any other algorithm — even using the right symmetric key — are
  rejected. Defense-in-depth against alg-confusion regressions in
  future library versions.
- `ClockSkew = 30s` (`JwtTokenServiceOptions.DefaultClockSkew`).
- **`OnAuthenticationFailed` logs** `ExceptionType` + `Method` + `Path`
  at Information level. Never logs the token content. Wired in the
  JwtBearer `Events` block.
- Missing / invalid / expired tokens on a non-`[Authorize]` endpoint
  leave `context.User` anonymous — request continues. `[Authorize]`
  endpoints return 401 via the standard authorization middleware.
- Endpoints that want to opt into role-gating use the existing
  `RequireRole` / `RequireSuperAdmin` / `RequirePlatformRole` fluent
  helpers on the endpoint builder; the shared
  `IdentityAuthorizationFilter` reads those off `Endpoint.Metadata`.

### Fail-closed startup guards

`IdentityServiceCollectionExtensions.EnforceProductionSecretHygiene`
runs at DI registration time and **refuses to boot** when either
dev-only fallback is in use outside `ASPNETCORE_ENVIRONMENT=Development`:

- `IDENTITY_JWT_SECRET_KEY` unset → the public fallback would sign tokens
  anyone could forge.
- `IDENTITY_TOTP_ENCRYPTION_KEY` unset → TOTP secrets would be AES-encrypted
  with a zero key (effectively plaintext).

Neither is a best-effort warning — the app throws at startup with a
clear message naming the missing env var. Covered by
`JwtHardeningContractTests.SecretHygiene_*` suite.

## Consumer services (ai-service, document-ingestion)

Both consumer services have a `Middleware/IdentityClaimsReader.cs` that
validates JWTs minted by main-backend. As of 2026-04-21, their
`IdentityClaimsReaderOptions` mirror the main-backend hardening:

- **`CreateValidationParameters()` builder** — single source of truth
  per consumer, same pattern as main-backend. Pinned `HS256`, all four
  `Validate*` flags on, `DefaultClockSkew = 30s`.
- **`PreviousSecretKey`** — reads `IDENTITY_JWT_SECRET_KEY_PREVIOUS` in
  `FromEnvironment()`. Accepts tokens signed by the previous key during
  a rotation window. **This is load-bearing:** without it, a main-backend
  key rotation would instantly start returning 401s from both consumer
  services until they themselves redeploy with the new secret.
- **`EnforceProductionSecretHygiene`** — refuses to boot if the public
  dev-fallback secret is in use outside Development. Accepts both
  `ASPNETCORE_ENVIRONMENT` (ai-service) and `DOTNET_ENVIRONMENT`
  (document-ingestion worker host) as the env signal.
- Consumer-side hardening is guarded by
  `tests/contract/IdentityClaimsReaderHardeningTests.cs` in each repo.

Required env vars across all three services:

| Variable | main-backend | ai-service | document-ingestion |
|---|---|---|---|
| `IDENTITY_JWT_SECRET_KEY` | required | required | required |
| `IDENTITY_JWT_SECRET_KEY_PREVIOUS` | rotation only | rotation only | rotation only |
| `IDENTITY_JWT_ISSUER` | recommended | recommended | recommended |
| `IDENTITY_JWT_AUDIENCE` | recommended | recommended | recommended |

All three services must share the same current + previous secret at all
times — a split where main-backend knows two keys but consumers only
know one means tokens minted after rotation are rejected downstream.

## JWT signing-key rotation

Zero-downtime rotation is supported via
`IDENTITY_JWT_SECRET_KEY_PREVIOUS`. The minter always signs with
`IDENTITY_JWT_SECRET_KEY` (current); the validator accepts tokens
signed by current OR previous.

Playbook (assumes `AccessTokenMinutes = 15`):

1. Generate a new key: `openssl rand -base64 48` (≥ 32 bytes required).
2. Deploy **all three services** (`main-backend`, `ai-service`,
   `document-ingestion`) simultaneously with:
   - `IDENTITY_JWT_SECRET_KEY` = **new** key
   - `IDENTITY_JWT_SECRET_KEY_PREVIOUS` = **old** key

   The three must roll together — if only main-backend has the new key,
   consumers reject the new tokens; if only consumers have both, old
   main-backend tokens work but no one mints new ones. Use your
   orchestrator's atomic deploy / config-map update.
3. Wait 16 minutes (access-token TTL + clock-skew buffer). Every active
   session's access token is now either (a) still valid but signed by
   old-key, or (b) expired and refreshed to a new-key token. Refresh
   tokens are opaque and unaffected.
4. Deploy all three services again with `IDENTITY_JWT_SECRET_KEY_PREVIOUS`
   **unset**.

No forced re-login, no session-table mutation. Covered by
`JwtHardeningContractTests.Rotation_*` in main-backend and
`IdentityClaimsReaderHardeningTests.Rotation_*` in each consumer repo.

## Local-dev: rebuilding the main-backend container

**Always use the `-p muallimi` project name and both compose files
together.** Any other invocation will create a container on a
different network / alias and the frontend's Next.js proxy will fail
with `getaddrinfo ENOTFOUND main-backend`.

```bash
cd muallimi-main-backend/infra
docker compose \
  -f docker-compose.local.yml \
  -f docker-compose.apps.yml \
  -p muallimi \
  up -d --build main-backend
```

If you've already created a stray container via a different compose
project and see `ENOTFOUND main-backend` from the frontend, the
targeted recovery is:

```bash
docker network connect --alias main-backend muallimi_default muallimi-main-backend
```

Verify DNS works from inside the frontend container before assuming
the code is broken:

```bash
docker exec muallimi-frontend wget -qO- http://main-backend:5063/health
```

Production / Kubernetes / ECS deploys don't go through docker-compose
at all — this gotcha is dev-only.

## Rollback

Each 2026-04-21 change is individually revertable:

- JSON policy flip → revert the `ConfigureHttpJsonOptions` block in
  `Program.cs`. Frontends continue to work (auth is already attributed;
  Phase 3 types would silently break again).
- `profile_ids` claim → remove from `JwtTokenService`. Frontend falls
  back to `localStorage` via `resolveStudentIdentity`.
- `Results.Forbid()` → `StatusCode(403)` — trivial to revert per site.
- Next.js rewrites → remove `rewrites()` block; relative `/api/*` calls
  from the browser 404 again.
- Migration `BackfillManagedStudentProfiles` → `Down()` is intentionally
  a no-op (reversing a data backfill is destructive). Dropping the
  inserted rows requires a separate, targeted SQL statement; avoid
  unless you know which profiles were backfilled vs. legitimately
  created.
