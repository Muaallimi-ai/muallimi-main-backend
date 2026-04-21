# Deploy Runbook — post-2026-04-21 changes

One-page operator guide for deploying the Phase 9 `profile_ids` JWT claim
migration, the Next.js API-proxy rewrites, and the global snake_case JSON
policy. Read this before the first push to any environment above local
dev.

## Required environment variables

### muallimi-main-backend

| Variable | Required in | Notes |
|---|---|---|
| `IDENTITY_JWT_SECRET_KEY` | All non-dev | ≥32 bytes. Current dev fallback (`dev-only-please-rotate-minimum-32-chars-secret-key`) MUST be overridden. |
| `IDENTITY_JWT_ISSUER` | Recommended | Defaults to `muallimi-main-backend`. Change only if rotating issuers. |
| `IDENTITY_JWT_AUDIENCE` | Recommended | Defaults to `muallimi-platform`. |
| `IDENTITY_TOTP_ENCRYPTION_KEY` | All non-dev | Base64 AES-256 key. Dev fallback is zeros (startup validation rejects in prod). |
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

## Latent issue flagged for a follow-up ticket

`builder.Services.AddAuthentication()` is NOT yet wired in
`Program.cs`. Without it:

- `context.User.Identity?.IsAuthenticated` is always false.
- `[Authorize]` / `.RequireAuthorization()` / `Results.Forbid()` on any
  endpoint throws `InvalidOperationException`.
- Inbound JWT signatures are not validated on Phase 3+ endpoints —
  tenant isolation today rests on the `X-Tenant-Id` header plus the EF
  global query filter, not on JWT validation.

This is a pre-existing architectural gap, not a regression from the
2026-04-21 work. A dedicated ticket covers wiring
`AddAuthentication().AddJwtBearer()` with the existing HS256 secret
and adding `UseAuthentication()` / `UseAuthorization()` to the
middleware pipeline before `UseIdentityTenantResolution()`.

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
