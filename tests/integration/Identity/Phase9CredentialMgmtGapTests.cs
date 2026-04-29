using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Credentials;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Credentials;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Application.Identity.Services;
using Muallimi.Application.Identity.Validators;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Domain.Parents;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Identity.Cryptography;
using Xunit;

namespace Muallimi.Api.Tests.Integration.Identity;

/// <summary>
/// Phase 9 Phase 5 — integration coverage for the closed credential-
/// management gaps. One test per claim from the Phase 9 memory:
///
///   1. Self-change rate-limit (5 / 15 min)
///   2. Successful self-change emits ChildPasswordChangedSelf audit
///   3. Wrong-current emits ChildPasswordChangeRejected with reason=wrong_current
///   4. Weak password emits ChildPasswordChangeRejected with reason=weak (and 422)
///   5. Forgot-password is anti-enumeration (silent success on unknown email)
///   6. ChildCredentialNotifier dedups inside the 24h window per (parent, child, kind)
///   7. ResetChildPin requires re-auth (401 reauth_required when receipt missing)
///   8. AddChildPin requires LoginMethod=profile_switch_only (tier_mismatch on Pin tier)
///   9. UpgradeChildToPassword requires LoginMethod=pin (tier_mismatch on under-8)
///  10. Parent-reset stamps PendingParentResetNoticeAt and login clears it after one read
///  11. ManagerReAuth.Verify rate-limits at 5/15min
///  12. User.SetPassword / SetPin bumps PasswordHashVersion (concurrency token)
///  13. Parent reset emits ParentResetChildPassword audit + revokes child refresh tokens
/// </summary>
public class Phase9CredentialMgmtGapTests
{
    // ── 1) Self-change rate-limit ────────────────────────────────────────

    [Fact]
    public async Task SelfChange_RateLimit_Triggers_After_5_Failed_Attempts()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (childId, _) = await SeedManagedChildAsync(h, "rl-child", "Real-Pw-1!", LoginMethods.UsernamePassword);
        var rl = new CountingRateLimitService();
        var svc = BuildPasswordResetService(h, rateLimits: rl);

        // First 5 wrong-current attempts → 401, no rate-limit trip yet.
        for (var i = 0; i < 5; i++)
        {
            var bad = await svc.ChangePasswordAsync(new ChangePasswordCommand(
                childId, "WrongPw-" + i + "!", "NewSecure-9!",
                "127.0.0.1", null, Guid.NewGuid().ToString("D")), Guid.Empty);
            Assert.False(bad.Success);
            Assert.Equal(401, bad.HttpStatus);
        }

        // 6th call returns 429 because the counter exceeded the budget.
        var sixth = await svc.ChangePasswordAsync(new ChangePasswordCommand(
            childId, "WrongPw-6!", "NewSecure-9!",
            "127.0.0.1", null, Guid.NewGuid().ToString("D")), Guid.Empty);
        Assert.False(sixth.Success);
        Assert.Equal(429, sixth.HttpStatus);
        Assert.Equal("rate_limited", sixth.ErrorCode);
    }

    // ── 2) Successful self-change emits credential audit ─────────────────

    [Fact]
    public async Task SelfChange_Success_Emits_ChildPasswordChangedSelf_Audit_And_Bumps_HashVersion()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (childId, _) = await SeedManagedChildAsync(h, "audit-child", "Real-Pw-1!", LoginMethods.UsernamePassword);
        var auditWriter = new InMemoryCredentialAuditWriter();
        var svc = BuildPasswordResetService(h, credentialAudit: auditWriter);

        var versionBefore = (await h.Db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == childId)).PasswordHashVersion;

        var ok = await svc.ChangePasswordAsync(new ChangePasswordCommand(
            childId, "Real-Pw-1!", "Wholly-Different-9!",
            "127.0.0.1", null, Guid.NewGuid().ToString("D")), Guid.Empty);
        Assert.True(ok.Success);

        Assert.Single(auditWriter.Events);
        Assert.Equal(CredentialAuditEventKind.ChildPasswordChangedSelf, auditWriter.Events[0].Kind);
        Assert.Equal(CredentialAuditActorTypes.User, auditWriter.Events[0].ActorType);
        Assert.Equal(childId, auditWriter.Events[0].TargetUserId);
        Assert.Equal(childId, auditWriter.Events[0].ActorId);

        // SetPassword must bump PasswordHashVersion (the optimistic-concurrency token).
        var versionAfter = (await h.Db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.Id == childId)).PasswordHashVersion;
        Assert.Equal(versionBefore + 1, versionAfter);
    }

    // ── 3) Wrong-current emits ChildPasswordChangeRejected with reason=wrong_current ──

    [Fact]
    public async Task SelfChange_WrongCurrent_Emits_Rejected_Audit_With_WrongCurrent_Reason()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (childId, _) = await SeedManagedChildAsync(h, "rej-child", "Real-Pw-1!", LoginMethods.UsernamePassword);
        var auditWriter = new InMemoryCredentialAuditWriter();
        var svc = BuildPasswordResetService(h, credentialAudit: auditWriter);

        var bad = await svc.ChangePasswordAsync(new ChangePasswordCommand(
            childId, "Wrong-Current-9!", "Wholly-Different-9!",
            "127.0.0.1", null, Guid.NewGuid().ToString("D")), Guid.Empty);

        Assert.False(bad.Success);
        Assert.Equal(401, bad.HttpStatus);
        Assert.Single(auditWriter.Events);
        Assert.Equal(CredentialAuditEventKind.ChildPasswordChangeRejected, auditWriter.Events[0].Kind);
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(auditWriter.Events[0].Payload);
        Assert.Contains(ChildPasswordChangeRejectionReasons.WrongCurrent, payloadJson);
    }

    // ── 4) Weak password rejection ───────────────────────────────────────

    [Fact]
    public async Task SelfChange_Weak_Password_Returns_422_And_Emits_Weak_Audit()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (childId, _) = await SeedManagedChildAsync(h, "weak-child", "Real-Pw-1!", LoginMethods.UsernamePassword);
        var auditWriter = new InMemoryCredentialAuditWriter();
        var svc = BuildPasswordResetService(h, credentialAudit: auditWriter);

        var weak = await svc.ChangePasswordAsync(new ChangePasswordCommand(
            childId, "Real-Pw-1!", "12345678",
            "127.0.0.1", null, Guid.NewGuid().ToString("D")), Guid.Empty);

        Assert.False(weak.Success);
        Assert.Equal(422, weak.HttpStatus);
        Assert.Equal("weak_password", weak.ErrorCode);
        Assert.Single(auditWriter.Events);
        Assert.Equal(CredentialAuditEventKind.ChildPasswordChangeRejected, auditWriter.Events[0].Kind);
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(auditWriter.Events[0].Payload);
        Assert.Contains(ChildPasswordChangeRejectionReasons.Weak, payloadJson);
    }

    // ── 5) Forgot-password anti-enumeration ──────────────────────────────

    [Fact]
    public async Task ForgotPassword_AntiEnum_Returns_Success_For_Unknown_Email_With_No_Notification()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var svc = BuildPasswordResetService(h);

        var outcome = await svc.ForgotPasswordAsync(new ForgotPasswordCommand(
            "nobody@example.com", "127.0.0.1", Guid.NewGuid().ToString("D")));

        Assert.True(outcome.Success);
        Assert.Empty(h.Notifications.Dispatched);
        // No reset_requested audit either — anti-enum requires zero side-effects.
        Assert.DoesNotContain(h.Audit.Events, e => e.Action == "reset_requested");
    }

    // ── 6) Notifier per-day dedup ────────────────────────────────────────

    [Fact]
    public async Task ChildCredentialNotifier_Dedups_Two_Events_Within_24h_Into_One_InboxRow()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("dedup-parent@example.com");
        var (childId, _) = await SeedManagedChildAsync(h, "dedup-child", "Real-Pw-1!", LoginMethods.UsernamePassword,
            parentId: parentId, parentTenantId: parentTenantId);
        await SeedParentProfileAsync(h, parentId, parentTenantId);
        var notifier = BuildChildCredentialNotifier(h);
        var child = await h.Db.IdentityUsers.IgnoreQueryFilters().FirstAsync(u => u.Id == childId);

        var corr1 = Guid.NewGuid().ToString("D");
        var corr2 = Guid.NewGuid().ToString("D");
        await notifier.NotifyChildPasswordChangedAsync(child, corr1);
        await notifier.NotifyChildPasswordChangedAsync(child, corr2);

        var rows = await h.Db.ParentNotifications.IgnoreQueryFilters()
            .Where(n => n.ChildId == childId
                     && n.NotificationKind == ChildCredentialNotifier.KindChildPasswordChanged)
            .ToListAsync();
        Assert.Single(rows);
        // The dedup path bumps CorrelationId on the existing row.
        Assert.Equal(corr2, rows[0].CorrelationId);
    }

    // ── 7) ResetChildPin requires re-auth ────────────────────────────────

    [Fact]
    public async Task ResetChildPin_Requires_ReAuth_Returns_401_When_Receipt_Missing()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("no-reauth@example.com");
        var (childId, _) = await SeedManagedChildAsync(h, "pin-child", null, LoginMethods.Pin,
            parentId: parentId, parentTenantId: parentTenantId, pinHash: BcryptHash("1234"));
        var staleReauth = new AlwaysFreshManagerReAuth { HasRecentResult = false };
        var svc = BuildUserManagementService(h, reauth: staleReauth);

        var outcome = await svc.ResetChildPinAsync(new ResetChildPinCommand(
            parentId, childId, "9876", "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(401, outcome.HttpStatus);
        Assert.Equal("reauth_required", outcome.ErrorCode);
    }

    // ── 8) AddChildPin tier guard (must be ProfileSwitchOnly) ────────────

    [Fact]
    public async Task AddChildPin_Rejects_When_Child_Already_On_Pin_Tier()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("tier-parent@example.com");
        var (childId, _) = await SeedManagedChildAsync(h, "tier-child", null, LoginMethods.Pin,
            parentId: parentId, parentTenantId: parentTenantId, pinHash: BcryptHash("1234"));
        var svc = BuildUserManagementService(h);

        var outcome = await svc.AddChildPinAsync(new AddChildPinCommand(
            parentId, childId, "5678", "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(409, outcome.HttpStatus);
        Assert.Equal("tier_mismatch", outcome.ErrorCode);
    }

    // ── 9) UpgradeChildToPassword tier guard (must be Pin) ───────────────

    [Fact]
    public async Task UpgradeChildToPassword_Rejects_When_Child_Is_ProfileSwitchOnly()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("upg-parent@example.com");
        var (childId, _) = await SeedManagedChildAsync(h, "upg-child", null, LoginMethods.ProfileSwitchOnly,
            parentId: parentId, parentTenantId: parentTenantId);
        var svc = BuildUserManagementService(h);

        var outcome = await svc.UpgradeChildToPasswordAsync(new UpgradeChildToPasswordCommand(
            parentId, childId, "Wholly-Different-9!", "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(409, outcome.HttpStatus);
        Assert.Equal("tier_mismatch", outcome.ErrorCode);
    }

    // ── 10) Parent reset notice surfaces once on next login, then clears ──

    [Fact]
    public async Task ParentReset_ChildLogin_Returns_Notice_Once_Then_Null()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("notice-parent@example.com");
        var (childId, childPassword) = await SeedManagedChildAsync(h, "notice-child", "ChildPw-1!", LoginMethods.UsernamePassword,
            parentId: parentId, parentTenantId: parentTenantId);
        var svc = BuildUserManagementService(h);
        var regen = await svc.RegenerateChildPasswordAsync(new RegenerateChildPasswordCommand(
            parentId, parentTenantId, childId, CustomPassword: "ParentChosen-9!",
            PasswordLocale: "ar", "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.True(regen.Success);

        // Notice marker landed.
        var afterReset = await h.Db.IdentityUsers.IgnoreQueryFilters().FirstAsync(u => u.Id == childId);
        Assert.NotNull(afterReset.PendingParentResetNoticeAt);

        // First login surfaces the marker (via AuthResponse.ParentResetNoticeAt) and clears it.
        var firstLogin = await h.AuthService.LoginAsync(new LoginCommand(
            "notice-child", "ParentChosen-9!", false, null, null,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.True(firstLogin.Success, firstLogin.Message);
        Assert.NotNull(firstLogin.Payload!.ParentResetNoticeAt);

        var afterFirstLogin = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(u => u.Id == childId);
        Assert.Null(afterFirstLogin.PendingParentResetNoticeAt);

        // Second login carries no notice.
        var secondLogin = await h.AuthService.LoginAsync(new LoginCommand(
            "notice-child", "ParentChosen-9!", false, null, null,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.True(secondLogin.Success);
        Assert.Null(secondLogin.Payload!.ParentResetNoticeAt);
    }

    // ── 11) ManagerReAuth.Verify rate-limits ─────────────────────────────

    [Fact]
    public async Task ManagerReAuth_Verify_RateLimits_At_5_Per_15min()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, _) = await h.SeedVerifiedParentAsync("rl-parent@example.com", "Real-Parent-9!");
        var rl = new CountingRateLimitService();
        var totp = new TwoFactorManagementService(
            h.Db, h.Passwords, new TotpTwoFactorService(),
            new AesEncryptor(new byte[32]), h.Audit.Emitter,
            NullLogger<TwoFactorManagementService>.Instance);
        var reauth = new InMemoryManagerReAuthService(h.Db, h.Passwords, totp, rl);

        // Burn 5 wrong-password attempts (each returns InvalidPassword).
        for (var i = 0; i < 5; i++)
        {
            var attempt = await reauth.VerifyAsync(parentId, "WrongPw-" + i + "!", null);
            Assert.Equal(ManagerReAuthOutcome.InvalidPassword, attempt);
        }

        // 6th attempt is rate-limited even with the correct password.
        var blocked = await reauth.VerifyAsync(parentId, "Real-Parent-9!", null);
        Assert.Equal(ManagerReAuthOutcome.RateLimited, blocked);
    }

    // ── 12) ManagerReAuth.Verify happy-path stamps a fresh receipt ───────

    [Fact]
    public async Task ManagerReAuth_Verify_Success_Stamps_Receipt_Honoured_By_HasRecent()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, _) = await h.SeedVerifiedParentAsync("freshness-parent@example.com", "Real-Parent-9!");
        var rl = new CountingRateLimitService();
        var totp = new TwoFactorManagementService(
            h.Db, h.Passwords, new TotpTwoFactorService(),
            new AesEncryptor(new byte[32]), h.Audit.Emitter,
            NullLogger<TwoFactorManagementService>.Instance);
        var reauth = new InMemoryManagerReAuthService(h.Db, h.Passwords, totp, rl);

        Assert.False(await reauth.HasRecentReAuthAsync(parentId));

        var ok = await reauth.VerifyAsync(parentId, "Real-Parent-9!", null);
        Assert.Equal(ManagerReAuthOutcome.Success, ok);
        Assert.True(await reauth.HasRecentReAuthAsync(parentId));

        // Invalidate clears the receipt.
        await reauth.InvalidateAsync(parentId);
        Assert.False(await reauth.HasRecentReAuthAsync(parentId));
    }

    // ── 13) Parent password reset emits credential audit + revokes refresh ──

    [Fact]
    public async Task ParentReset_Emits_ParentResetChildPassword_Audit_And_Revokes_Refresh_Tokens()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("audit2-parent@example.com");
        var (childId, _) = await SeedManagedChildAsync(h, "audit2-child", "Real-Pw-1!", LoginMethods.UsernamePassword,
            parentId: parentId, parentTenantId: parentTenantId);
        // Seed a live refresh token so we can prove revocation.
        var liveToken = new Muallimi.Domain.Identity.Entities.RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = childId,
            SessionId = Guid.NewGuid(),
            TokenHash = "fake-hash",
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        };
        h.Db.IdentityRefreshTokens.Add(liveToken);
        await h.Db.SaveChangesAsync();

        var auditWriter = new InMemoryCredentialAuditWriter();
        var svc = BuildUserManagementService(h, credentialAudit: auditWriter);

        var regen = await svc.RegenerateChildPasswordAsync(new RegenerateChildPasswordCommand(
            parentId, parentTenantId, childId, CustomPassword: null,
            PasswordLocale: "ar", "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.True(regen.Success);

        // Credential audit row of the right kind, with parent as actor + child as target.
        Assert.Single(auditWriter.Events);
        var evt = auditWriter.Events[0];
        Assert.Equal(CredentialAuditEventKind.ParentResetChildPassword, evt.Kind);
        Assert.Equal(parentId, evt.ActorId);
        Assert.Equal(childId, evt.TargetUserId);

        // Live refresh token is now revoked.
        var token = await h.Db.IdentityRefreshTokens.IgnoreQueryFilters()
            .AsNoTracking().FirstAsync(t => t.Id == liveToken.Id);
        Assert.NotNull(token.RevokedAt);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string BcryptHash(string s) => new BCryptPasswordService().Hash(s);

    private async Task<(Guid ChildId, string? Password)> SeedManagedChildAsync(
        IdentityTestHarness h,
        string username,
        string? password,
        string loginMethod,
        Guid? parentId = null,
        Guid? parentTenantId = null,
        string? pinHash = null)
    {
        Guid pid;
        Guid tid;
        if (parentId is null || parentTenantId is null)
        {
            (pid, tid) = await h.SeedVerifiedParentAsync(username + "-parent@example.com");
        }
        else
        {
            pid = parentId.Value;
            tid = parentTenantId.Value;
        }

        var childId = Guid.NewGuid();
        var child = new User
        {
            Id = childId,
            TenantId = tid,
            AccountType = AccountType.Managed,
            ManagedByUserId = pid,
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            FullName = "Child " + username,
            Locale = "ar",
            Status = UserStatus.Active,
            PasswordHash = string.IsNullOrEmpty(password) ? null : h.Passwords.Hash(password),
            PasswordChangedAt = string.IsNullOrEmpty(password) ? null : DateTime.UtcNow,
            LoginMethod = loginMethod,
            PinHash = pinHash,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = pid,
        };
        h.Db.IdentityUsers.Add(child);

        var studentRole = await h.Db.IdentityRoles.IgnoreQueryFilters()
            .FirstAsync(r => r.Name == "student");
        h.Db.IdentityUserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = childId,
            RoleId = studentRole.Id,
            TenantId = tid,
            GrantedBy = pid,
            GrantedAt = DateTime.UtcNow,
        });

        // Minimal StudentProfile so the credential notifier can resolve grade/birthday.
        h.Db.StudentProfiles.Add(new StudentProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tid,
            UserId = childId,
            DisplayName = child.FullName,
            CurriculumType = "Moe",
            Grade = "5",
            PreferredLanguage = "ar",
            PlanTier = "free",
            SubjectsEnrolled = "[]",
            ConsentState = "granted",
            Birthday = new DateOnly(2015, 1, 1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();
        return (childId, password);
    }

    private async Task SeedParentProfileAsync(IdentityTestHarness h, Guid parentId, Guid tenantId)
    {
        h.Db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = Guid.NewGuid(),
            TenantId = tenantId,
            IdentityId = parentId,
            UserId = parentId,
            PreferredLanguage = "ar",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();
    }

    private static PasswordResetService BuildPasswordResetService(
        IdentityTestHarness h,
        IRateLimitService? rateLimits = null,
        ICredentialAuditWriter? credentialAudit = null,
        IChildCredentialNotifier? childNotifier = null)
        => new(
            h.Db, h.Passwords, h.Sessions,
            new SessionCascadeService(h.Db, new Muallimi.Infrastructure.Identity.Adapters.InMemorySessionActivityCache()),
            h.Audit.Emitter, h.Notifications,
            new PasswordResetLinkBuilder("http://test.local"),
            rateLimits ?? new NullRateLimitService(),
            new ZxcvbnPasswordStrengthValidator(),
            credentialAudit ?? new InMemoryCredentialAuditWriter(),
            new AlwaysFreshManagerReAuth(),
            childNotifier ?? new CapturingChildCredentialNotifier(),
            NullLogger<PasswordResetService>.Instance);

    private static UserManagementService BuildUserManagementService(
        IdentityTestHarness h,
        IManagerReAuthService? reauth = null,
        ICredentialAuditWriter? credentialAudit = null)
        => new(
            h.Db, h.Passwords,
            new UsernameGenerator(new Random(1234)),
            new ChildPasswordGenerator(new Random(4321)),
            h.Audit.Emitter, h.Notifications,
            NullLogger<UserManagementService>.Instance,
            new WeakPinBlocklist(),
            reauth ?? new AlwaysFreshManagerReAuth(),
            credentialAudit ?? new InMemoryCredentialAuditWriter(),
            new ZxcvbnPasswordStrengthValidator());

    private static ChildCredentialNotifier BuildChildCredentialNotifier(IdentityTestHarness h)
        => new(
            h.Db,
            new ManagedUserNotificationRecipients(h.Db),
            new Muallimi.Api.Parents.ParentNotifications.ParentNotificationRepository(h.Db),
            h.Notifications,
            NullLogger<ChildCredentialNotifier>.Instance);
}

/// <summary>
/// Counting in-memory rate limiter for tests that need to observe the
/// 5/15min lockout actually trip. Uses a process-static dictionary
/// keyed by <c>(scope, key)</c> so successive scoped instances see the
/// same counter (mirrors RedisRateLimitService semantics).
/// </summary>
internal sealed class CountingRateLimitService : IRateLimitService
{
    private static readonly ConcurrentDictionary<string, (int count, DateTime until)> _counters = new();

    public Task<RateLimitDecision> IncrementAndCheckAsync(
        string scope, string key, int maxAttempts, TimeSpan window, CancellationToken ct = default)
    {
        var k = scope + ":" + key;
        var now = DateTime.UtcNow;
        var entry = _counters.AddOrUpdate(k,
            _ => (1, now.Add(window)),
            (_, prev) => prev.until <= now ? (1, now.Add(window)) : (prev.count + 1, prev.until));
        var allowed = entry.count <= maxAttempts;
        return Task.FromResult(new RateLimitDecision(
            Allowed: allowed,
            CurrentCount: entry.count,
            MaxAttempts: maxAttempts,
            RetryAfter: allowed ? null : entry.until - now));
    }

    public Task<bool> IsLockedOutAsync(string userIdentifier, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task LockOutAsync(string userIdentifier, TimeSpan duration, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearLockoutAsync(string userIdentifier, CancellationToken ct = default)
    {
        // Match the production semantics: ChangePasswordAsync calls Clear with
        // the FULL "scope:key" string (so we strip the leading prefix).
        foreach (var k in _counters.Keys)
        {
            if (k == userIdentifier || k.EndsWith(":" + userIdentifier, StringComparison.Ordinal))
                _counters.TryRemove(k, out _);
        }
        return Task.CompletedTask;
    }
}
