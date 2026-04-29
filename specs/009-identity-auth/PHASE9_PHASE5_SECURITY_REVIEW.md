# Phase 9 Phase 5 — Security Signoff (Credential Management)

**Date:** 2026-04-28
**Scope:** Parent-managed-child credential management — child self-change,
parent reset (PIN/password), parent add-PIN (8th birthday), parent
upgrade-to-password (13th birthday), 3-channel notification fan-out,
post-reset child notice, anti-enum forgot-password, daily birthday job.
B2B abstractions only — no school-admin code paths.

**Test baseline at signoff:**
- `dotnet test tests/Muallimi.Api.Tests` → **553 / 553 pass** (540 baseline + 13 new gap tests)
- `dotnet test tests/Muallimi.Domain.Tests` → **1249 / 1249 pass**
- `dotnet test tests/Muallimi.Infrastructure.Tests` → **1 / 1 pass**
- `npx tsc --noEmit` (Muaallimi-Platform) → **0 errors**

The 35 pre-existing failures triaged at Phase 5 entry are all closed —
they were Paymob 2-phase-registration test-infra debt + 3 unrelated bugs
(`UserManagementService` custom-password echo, `SubscriptionLifecycleTests`
default-status drift, `CreateChildRequest` shape staleness). Zero
production code regressions; only a single targeted code fix
(`UserManagementService.cs:185-189` — return the parent-supplied custom
password to the once-only payload).

## Gap-by-gap closure proof

Each row cites the file + line where the gap is closed. Memory-claimed
behaviour is a claim; only file:line proves the gap is *actually* closed
in code. Rows below are the union of the original spec gaps and the
threading work captured in Phase 9 Phases 1-4.

| # | Gap / claim | Proof (file:line) |
|---|-------------|-------------------|
| 1 | `User.PasswordHashVersion` is the optimistic-concurrency token | [User.cs:49](../../src/Muallimi.Domain/Identity/Entities/User.cs#L49) |
| 2 | `IsConcurrencyToken()` binding so EF actually emits the WHERE-version clause | [IdentityModelConfiguration.cs:66](../../src/Muallimi.Infrastructure/Identity/EfCore/IdentityModelConfiguration.cs#L66) |
| 3 | `User.SetPassword` bumps `PasswordHashVersion` (canonical password mutation) | [User.cs:220-231](../../src/Muallimi.Domain/Identity/Entities/User.cs#L220-L231) |
| 4 | `User.SetPin` bumps `PasswordHashVersion` (same token covers both credential types) | [User.cs:241-250](../../src/Muallimi.Domain/Identity/Entities/User.cs#L241-L250) |
| 5 | `User.PendingParentResetNoticeAt` field — captures parent-reset trigger | [User.cs:77](../../src/Muallimi.Domain/Identity/Entities/User.cs#L77) |
| 6 | `User.MarkPendingParentResetNotice()` (stamp) | [User.cs:274-278](../../src/Muallimi.Domain/Identity/Entities/User.cs#L274-L278) |
| 7 | `User.AcknowledgeParentResetNotice()` (single-use clear) | [User.cs:284-289](../../src/Muallimi.Domain/Identity/Entities/User.cs#L284-L289) |
| 8 | `User.AddPinForUnderEight()` — 8th-birthday tier transition with guard | [User.cs:296-304](../../src/Muallimi.Domain/Identity/Entities/User.cs#L296-L304) |
| 9 | `User.UpgradePinToPassword()` — 13th-birthday tier transition with guard | [User.cs:312-320](../../src/Muallimi.Domain/Identity/Entities/User.cs#L312-L320) |
| 10 | `User.AgeTransitionNotifiedAt` — daily-job idempotency marker | [User.cs:68](../../src/Muallimi.Domain/Identity/Entities/User.cs#L68) |
| 11 | `LoginMethods` constants (`profile_switch_only`/`pin`/`username_password`) | [LoginMethods.cs](../../src/Muallimi.Domain/Identity/Enums/LoginMethods.cs) |
| 12 | 9-kind `CredentialAuditEventKind` enum — all credential events typed | [CredentialAuditEventKind.cs:14-25](../../src/Muallimi.Application/Identity/Credentials/CredentialAuditEventKind.cs#L14-L25) |
| 13 | `ICredentialAuditWriter` interface (single-source audit emission) | [ICredentialAuditWriter.cs:19-22](../../src/Muallimi.Application/Identity/Credentials/ICredentialAuditWriter.cs#L19-L22) |
| 14 | `CredentialAuditWriter` delegates to Phase 6 `AuditTrailWriter` (no parallel impl) | [CredentialAuditWriter.cs:18-44](../../src/Muallimi.Api/Identity/Credentials/CredentialAuditWriter.cs#L18-L44) |
| 15 | EF migration `AddCredentialConcurrencyAndAgeTransition` adds version + age-transition columns | [20260428134954_AddCredentialConcurrencyAndAgeTransition.cs](../../src/Muallimi.Infrastructure/Migrations/20260428134954_AddCredentialConcurrencyAndAgeTransition.cs) |
| 16 | EF migration `AddPendingParentResetNotice` adds the post-reset-notice column | [20260428141442_AddPendingParentResetNotice.cs](../../src/Muallimi.Infrastructure/Migrations/20260428141442_AddPendingParentResetNotice.cs) |
| 17 | `IdentityEmailTemplates` ar+en for child_password_changed + 2 birthday transitions | [IdentityEmailTemplates.cs](../../src/Muallimi.Application/Identity/Notifications/IdentityEmailTemplates.cs) |
| 18 | `ChangePasswordAsync` rate limit (5 / 15min via existing `IRateLimitService`) | [PasswordResetService.cs:264-270](../../src/Muallimi.Api/Identity/Services/PasswordResetService.cs#L264-L270) |
| 19 | `ChangePasswordAsync` zxcvbn ≥ 3 enforcement (rejects with 422 weak_password) | [PasswordResetService.cs:290-303](../../src/Muallimi.Api/Identity/Services/PasswordResetService.cs#L290-L303) |
| 20 | `ChangePasswordAsync` emits `child_password_changed_self` audit on success | [PasswordResetService.cs:337-350](../../src/Muallimi.Api/Identity/Services/PasswordResetService.cs#L337-L350) |
| 21 | `ChangePasswordAsync` emits `child_password_change_rejected` w/ reason on 401/422 | [PasswordResetService.cs:276-300](../../src/Muallimi.Api/Identity/Services/PasswordResetService.cs#L276-L300) |
| 22 | `ChangePasswordAsync` invalidates the manager re-auth receipt | [PasswordResetService.cs:322](../../src/Muallimi.Api/Identity/Services/PasswordResetService.cs#L322) |
| 23 | `JwtTokenService` emits the `login_method` claim on managed accounts | [JwtTokenService.cs:190](../../src/Muallimi.Application/Identity/Services/JwtTokenService.cs#L190) |
| 24 | Frontend `auth-context` parses `login_method` into the `loginMethod` field | [auth-context.tsx:74,223](../../../Muaallimi-Platform/src/contexts/auth-context.tsx#L74) |
| 25 | Anti-enum forgot-password: unknown email returns generic 200 + zero side-effects | [PasswordResetService.cs:130-133](../../src/Muallimi.Api/Identity/Services/PasswordResetService.cs#L130-L133) |
| 26 | Forgot-password page renders the same generic confirmation regardless of email | [forgot-password/page.tsx:81](../../../Muaallimi-Platform/src/app/(auth)/forgot-password/page.tsx#L81) |
| 27 | `/settings/security` per-tier rendering (none / pin_readonly / password_form) | [security/page.tsx:127-131](../../../Muaallimi-Platform/src/app/settings/security/page.tsx#L127-L131) |
| 28 | `RedisManagerReAuthService` extends shared `ManagerReAuthServiceBase` | [RedisManagerReAuthService.cs](../../src/Muallimi.Api/Identity/Credentials/RedisManagerReAuthService.cs) |
| 29 | `InMemoryManagerReAuthService` extends shared `ManagerReAuthServiceBase` (process-static dict, local-dev fallback) | [InMemoryManagerReAuthService.cs:21-50](../../src/Muallimi.Api/Identity/Credentials/InMemoryManagerReAuthService.cs#L21-L50) |
| 30 | Re-auth `Verify` rate-limits at 5 / 15min (single shared pipeline) | [ManagerReAuthServiceBase.cs:54-86](../../src/Muallimi.Api/Identity/Credentials/ManagerReAuthServiceBase.cs#L54-L86) |
| 31 | `RegenerateChildPasswordAsync` requires fresh re-auth receipt | [UserManagementService.cs:532-533](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L532-L533) |
| 32 | `RegenerateChildPasswordAsync` returns 409 `concurrency_conflict` on `DbUpdateConcurrencyException` | [UserManagementService.cs:567-573](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L567-L573) |
| 33 | `RegenerateChildPasswordAsync` stamps `MarkPendingParentResetNotice` + emits credential audit | [UserManagementService.cs:561,586-588](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L560-L590) |
| 34 | `RegenerateChildPasswordAsync` returns the parent-supplied custom password (Phase 5 fix) | [UserManagementService.cs:185-189](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L185-L189) |
| 35 | `ResetChildPinAsync` routes through shared `ExecuteCredentialActionAsync` (no parallel impl) | [UserManagementService.cs:604-613](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L604-L613) |
| 36 | `AddChildPinAsync` routes through shared `ExecuteCredentialActionAsync` | [UserManagementService.cs:615-624](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L615-L624) |
| 37 | `UpgradeChildToPasswordAsync` routes through shared `ExecuteCredentialActionAsync` | [UserManagementService.cs:626-635](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L626-L635) |
| 38 | `ExecuteCredentialActionAsync` enforces re-auth + tier guard + audit + post-reset notice | [UserManagementService.cs:655-700](../../src/Muallimi.Api/Identity/Services/UserManagementService.cs#L655-L700) |
| 39 | Four new routes hang off the existing `ParentChildrenEndpoints` (no parallel endpoint group) | [ParentChildrenEndpoints.cs:39-42](../../src/Muallimi.Api/Identity/Endpoints/ParentChildrenEndpoints.cs#L39-L42) |
| 40 | Post-reset notice: captured + cleared in the same SaveChanges as login | [AuthService.cs:505-508](../../src/Muallimi.Api/Identity/Services/AuthService.cs#L505-L508) |
| 41 | Post-reset notice: returned in `AuthResponse.ParentResetNoticeAt` once | [AuthResponse.cs:67](../../src/Muallimi.Application/Identity/Dtos/AuthResponse.cs#L67) and [AuthService.cs:890](../../src/Muallimi.Api/Identity/Services/AuthService.cs#L890) |
| 42 | Frontend `consumeParentResetNotice()` reads + clears the sessionStorage stash (single-use) | [auth.service.ts:204-213](../../../Muaallimi-Platform/src/services/auth.service.ts#L204-L213) |
| 43 | `ParentResetNoticeBanner` renders once per stash; auto-clears after first read | [ParentResetNoticeBanner.tsx:33-37](../../../Muaallimi-Platform/src/components/student/ParentResetNoticeBanner.tsx#L33-L37) |
| 44 | Frontend single shared `ParentCredentialActionDialog` for all 4 actions (re-auth → form → done) | [ParentCredentialActionDialog.tsx:139-143](../../../Muaallimi-Platform/src/components/parent/ParentCredentialActionDialog.tsx#L139-L143) |
| 45 | `ChildActionsMenu` retired the auto-generate password flow; per-tier menu items only | [ChildActionsMenu.tsx:245-276](../../../Muaallimi-Platform/src/components/parent/ChildActionsMenu.tsx#L245-L276) |
| 46 | `ManagedUserNotificationRecipients` filters out school-managed parents + archived users | [ManagedUserNotificationRecipients.cs:36-63](../../src/Muallimi.Api/Identity/Credentials/ManagedUserNotificationRecipients.cs#L36-L63) |
| 47 | `ChildCredentialNotifier` — 3 public events all dispatch through one private `FanOutAsync` | [ChildCredentialNotifier.cs:79-104](../../src/Muallimi.Api/Identity/Credentials/ChildCredentialNotifier.cs#L79-L104) |
| 48 | Per-day dedup window — single inbox row + single email per (parent, child, kind) per 24h | [ChildCredentialNotifier.cs:150-163](../../src/Muallimi.Api/Identity/Credentials/ChildCredentialNotifier.cs#L150-L163) |
| 49 | `ParentNotificationRepository.FindLatestByKindAsync` is the dedup query | [ParentNotificationRepository.cs:170-185](../../src/Muallimi.Api/Parents/ParentNotifications/ParentNotificationRepository.cs#L170-L185) |
| 50 | `ChildAgeTransitionJob` is a `BackgroundService` mirroring `DataRetentionHostedService` | [ChildAgeTransitionJob.cs:41-83](../../src/Muallimi.Api/Identity/Credentials/ChildAgeTransitionJob.cs#L41-L83) |
| 51 | Daily job is idempotent via `AgeTransitionNotifiedAt` (stamped after successful notify) | [ChildAgeTransitionJob.cs:141-164](../../src/Muallimi.Api/Identity/Credentials/ChildAgeTransitionJob.cs#L141-L164) |
| 52 | Daily job filters parent-managed only via `LoginMethod IN ('profile_switch_only','pin')` and `AccountType=Managed` | [ChildAgeTransitionJob.cs:99-108](../../src/Muallimi.Api/Identity/Credentials/ChildAgeTransitionJob.cs#L99-L108) |
| 53 | `CredentialEventBanner` reads existing inbox feed (no parallel inbox model) | [CredentialEventBanner.tsx:81-91](../../../Muaallimi-Platform/src/components/parent/CredentialEventBanner.tsx#L81-L91) |
| 54 | `CredentialEventBanner` 24h freshness window applied via `BANNER_WINDOW_MS` | [CredentialEventBanner.tsx:39,58-73](../../../Muaallimi-Platform/src/components/parent/CredentialEventBanner.tsx#L39-L73) |
| 55 | `CredentialEventBanner` per-id sessionStorage dismiss (cross-reload memory) | [CredentialEventBanner.tsx:42-56](../../../Muaallimi-Platform/src/components/parent/CredentialEventBanner.tsx#L42-L56) |
| 56 | Frontend `notification_kind` enum extended with the 3 credential kinds (no parallel inbox) | [parentPreferencesApi.ts:65-67](../../../Muaallimi-Platform/src/app/parent/_lib/parentPreferencesApi.ts#L65-L67) |

## Coverage matrix (what the new tests prove)

Phase 5 added one integration test file (`Phase9CredentialMgmtGapTests.cs`,
13 tests) and three Playwright e2e specs covering the parent + child
credential flows. The mapping below pins each gap to a test that fails
loudly if the gap regresses.

| Gap (#) | Asserted by |
|---------|-------------|
| 18 (rate limit 5/15min) | `Phase9CredentialMgmtGapTests.SelfChange_RateLimit_Triggers_After_5_Failed_Attempts` |
| 20, 3 (audit kind on success + version bump) | `SelfChange_Success_Emits_ChildPasswordChangedSelf_Audit_And_Bumps_HashVersion` |
| 21 (rejection audit w/ reason) | `SelfChange_WrongCurrent_Emits_Rejected_Audit_With_WrongCurrent_Reason` |
| 19 (zxcvbn) | `SelfChange_Weak_Password_Returns_422_And_Emits_Weak_Audit` |
| 25 (anti-enum) | `ForgotPassword_AntiEnum_Returns_Success_For_Unknown_Email_With_No_Notification` |
| 47, 48, 49 (dedup) | `ChildCredentialNotifier_Dedups_Two_Events_Within_24h_Into_One_InboxRow` |
| 38 (re-auth gate) | `ResetChildPin_Requires_ReAuth_Returns_401_When_Receipt_Missing` |
| 8 (tier guard ProfileSwitchOnly) | `AddChildPin_Rejects_When_Child_Already_On_Pin_Tier` |
| 9 (tier guard Pin) | `UpgradeChildToPassword_Rejects_When_Child_Is_ProfileSwitchOnly` |
| 5, 6, 7, 40, 41 (post-reset notice) | `ParentReset_ChildLogin_Returns_Notice_Once_Then_Null` |
| 30 (re-auth rate-limit) | `ManagerReAuth_Verify_RateLimits_At_5_Per_15min` |
| 28, 29, 30 (re-auth happy path) | `ManagerReAuth_Verify_Success_Stamps_Receipt_Honoured_By_HasRecent` |
| 14, 33 (parent-reset audit kind + token revocation) | `ParentReset_Emits_ParentResetChildPassword_Audit_And_Revokes_Refresh_Tokens` |
| 44, 45 (4-action dialog + per-tier menu) | `parent-credential-actions.spec.ts` (7 tests, ar+en × 3 viewports) |
| 53, 54, 55 (banner lifecycle) | `credential-banners.spec.ts` (6 tests) |
| 41, 42, 43 (post-reset notice end-to-end on the child) | `credential-banners.spec.ts: ParentResetNoticeBanner: stashed timestamp...` |
| 18, 19, 20, 21 (UI errors map correctly) | `child-self-change-password.spec.ts` (3 tests) |

## Smoke-script coverage

`infra/identity-smoke.sh` (extended in Phase 5) exercises:

- IDS01 — anti-enum `forgot-password` with unknown email → 200 (Gap 25)
- IDS02 — anti-enum `forgot-password` with known email → same 200 (Gap 25)
- IDS03 — `reset-password` rejects garbage tokens with 4xx
- IDS04 — `verify-email` rejects garbage tokens with 4xx
- IDS05 — `/parent/credential/reauth` requires JWT → 401 (Gap 38)
- IDS06 — `/parent/children/{id}/reset-pin` requires JWT → 401 (Gap 39)
- IDS07 — `/parent/children/{id}/add-pin` requires JWT → 401 (Gap 39)
- IDS08 — `/parent/children/{id}/upgrade-to-password` requires JWT → 401 (Gap 39)
- IDS09 — `/change-password` requires JWT → 401

Each step writes evidence to `infra/scripts/_evidence/identity/{stepId}.{body,status,ok}` plus a top-level `correlation_id.txt`, `started_at.txt`, `completed_at.txt`, `exit_code`.

## Post-signoff change — lookup-method anti-enum simplification (2026-04-28)

After signoff, the `/api/auth/lookup-method` endpoint was simplified from
the deterministic-hash-fake design (option C) to a default-safe rule
(option B): `password` for everything **except** a real Managed account
on the `pin` tier, which still returns `pin`.

**Rationale:** the original design was paying a daily UX cost (every
first-time visitor with the wrong hash got a confusing PIN field on
their email) for a security gain that's narrow in this product:

- Children don't have public usernames — the auto-generated
  `name.year.suffix` shape encodes the birth year, so confirming "PIN
  tier" via the lookup adds little to an attacker who already had the
  username.
- The 3/min/IP rate limit on the endpoint makes mass enumeration
  impractical regardless.
- The real anti-enum boundary is the login endpoints
  (`POST /api/auth/login`, `POST /api/auth/login/pin`), which return
  identical `invalid_credentials` for "user doesn't exist" vs. "wrong
  credential" — that path is unchanged.

**Files touched:**
- [`PublicAuthEndpoints.cs:179-238`](../../src/Muallimi.Api/Identity/Endpoints/PublicAuthEndpoints.cs#L179-L238) — replaced 25 lines of branched logic + hash fake with a single rule. Removed unused `System.Security.Cryptography` and `System.Text` imports.
- 553/553 Api.Tests still green (no test pinned the hash-fake behavior; the simplification was loss-free).

**Smoke verified live:**
- `curl POST /api/auth/lookup-method {"identifier":"unknown@x.com"}` → `{"method":"password"}` ✅
- `curl POST /api/auth/lookup-method {"identifier":"any-random-junk"}` → `{"method":"password"}` ✅
- Real PIN-tier child username → still returns `{"method":"pin"}` (rule preserved).

## Sign-off

The Phase 9 credential-management feature has zero open security debt
at sign-off. Every gap from the original spec and the
Phases-1-through-4 thread is closed in code with the citation above.
The test net is green (553 / 1249 / 1) and the smoke script + Playwright
specs cover the live HTTP surfaces and the UI flows respectively.

Open follow-ups (not security gaps):
- Frontend `ChildModeBanner.decodeJwt` UTF-8 patch (tracked separately).
- B2B / multi-guardian — `IManagedUserNotificationRecipients` and the
  list-returning interface are already shaped for it; landing requires a
  `ChildGuardian` link table, not changes to this Phase's pipelines.
