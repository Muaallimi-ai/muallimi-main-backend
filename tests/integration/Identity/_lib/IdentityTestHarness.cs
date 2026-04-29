using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Audit;
using Muallimi.Application.Identity.Credentials;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Application.Identity.Services;
using Muallimi.Application.Identity.Validators;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Identity.Adapters;
using Muallimi.Infrastructure.Identity.Cryptography;
using Muallimi.Infrastructure.Identity.Seed;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Tests.Identity;

/// <summary>
/// Phase 9 US1 integration harness — mirrors the Phase5TestDbContext
/// pattern: EF Core InMemory <see cref="MuallimiDbContext"/> with the
/// pgvector-backed entities ignored, plus the Identity services wired
/// for in-process testing.
/// </summary>
public sealed class IdentityTestHarness : IDisposable
{
    public const string JwtSecret = "test-secret-key-32-bytes-min-ok!!-pad";

    private sealed class TestDbContext : MuallimiDbContext
    {
        public TestDbContext(DbContextOptions<MuallimiDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            mb.Ignore<Muallimi.Domain.Curriculum.ContentChunk>();
            mb.Ignore<Muallimi.Domain.Curriculum.QaCacheEntry>();
        }
    }

    public MuallimiDbContext Db { get; }
    public IAuthService AuthService { get; }
    public IEmailVerificationService Verification { get; }
    public ITokenService Tokens { get; }
    public IPasswordService Passwords { get; }
    public ISessionService Sessions { get; }
    public InMemoryNotificationSpy Notifications { get; }
    public AuditEventSpy Audit { get; }
    public IPasswordResetService PasswordResetService { get; }
    public ITwoFactorManagementService TwoFactorManagement { get; }
    public Muallimi.Api.Identity.Services.IImpersonationService ImpersonationService { get; }

    private IdentityTestHarness(
        MuallimiDbContext db,
        IAuthService auth,
        IEmailVerificationService verification,
        ITokenService tokens,
        IPasswordService passwords,
        ISessionService sessions,
        InMemoryNotificationSpy notifications,
        AuditEventSpy audit,
        IPasswordResetService passwordResetService,
        ITwoFactorManagementService twoFactorManagement,
        Muallimi.Api.Identity.Services.IImpersonationService impersonationService)
    {
        Db = db;
        AuthService = auth;
        Verification = verification;
        Tokens = tokens;
        Passwords = passwords;
        Sessions = sessions;
        Notifications = notifications;
        Audit = audit;
        PasswordResetService = passwordResetService;
        TwoFactorManagement = twoFactorManagement;
        ImpersonationService = impersonationService;
    }

    public static async Task<IdentityTestHarness> CreateAsync(CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase($"identity-us1-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

        // Seed the 8 system roles and the Platform tenant so AuthService
        // can resolve the parent / school-admin grants.
        await SeedRolesAndPlatformTenantAsync(db, ct).ConfigureAwait(false);

        var audit = new AuditEventSpy();
        var passwords = new BCryptPasswordService();
        var tokens = new JwtTokenService(new JwtTokenServiceOptions
        {
            SecretKey = JwtSecret,
            Issuer = "muallimi-main-backend",
            Audience = "muallimi-platform",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        });
        var rateLimit = new NullRateLimitService();
        var sessionRepo = new Muallimi.Infrastructure.Identity.Adapters.EfSessionRepository(db);
        var sessionCache = new Muallimi.Infrastructure.Identity.Adapters.InMemorySessionActivityCache();
        var sessions = new SessionService(sessionRepo, sessionCache);

        var notifications = new InMemoryNotificationSpy();
        var strength = new ZxcvbnPasswordStrengthValidator();
        var verification = new EmailVerificationService(db, audit.Emitter, NullLogger<EmailVerificationService>.Instance);
        var linkBuilder = new VerificationLinkBuilder("http://test.local");

        var devKey = new byte[32];
        var aes = new AesEncryptor(devKey);
        var twoFactorService = new TotpTwoFactorService();
        var twoFactorMgmt = new TwoFactorManagementService(
            db, passwords, twoFactorService, aes, audit.Emitter,
            NullLogger<TwoFactorManagementService>.Instance);

        var profileIds = new Muallimi.Application.Identity.Services.ProfileIdsResolver(
            new Muallimi.Application.Identity.Services.IProfileIdContributor[]
            {
                new Muallimi.Api.Identity.Services.StudentProfileIdContributor(db),
            });

        // Add-child redesign Phase 5: cascade-revoke and Phase 7.3:
        // subscription-expiry guard now factor into auth + reset flows.
        var sessionCascade = new SessionCascadeService(db, sessionCache);
        var subscriptionGuard = new SubscriptionGuard(db);

        var auth = new AuthService(
            db, passwords, tokens, rateLimit, sessions, audit.Emitter,
            notifications, verification, linkBuilder, profileIds,
            sessionCascade, subscriptionGuard,
            NullLogger<AuthService>.Instance,
            twoFactorMgmt);

        var resetLinkBuilder = new PasswordResetLinkBuilder("http://test.local");
        var passwordStrength = new ZxcvbnPasswordStrengthValidator();
        var credentialAudit = new InMemoryCredentialAuditWriter();
        var reauth = new AlwaysFreshManagerReAuth();
        var childNotifier = new CapturingChildCredentialNotifier();
        var pwReset = new PasswordResetService(
            db, passwords, sessions, sessionCascade, audit.Emitter, notifications,
            resetLinkBuilder, rateLimit, passwordStrength, credentialAudit, reauth, childNotifier,
            NullLogger<PasswordResetService>.Instance);

        var impersonation = new Muallimi.Api.Identity.Services.ImpersonationService(
            db, tokens, audit.Emitter, profileIds,
            NullLogger<Muallimi.Api.Identity.Services.ImpersonationService>.Instance);

        return new IdentityTestHarness(db, auth, verification, tokens, passwords, sessions,
            notifications, audit, pwReset, twoFactorMgmt, impersonation);
    }

    private static async Task SeedRolesAndPlatformTenantAsync(MuallimiDbContext db, CancellationToken ct)
    {
        if (!await db.IdentityTenants.AnyAsync(t => t.Type == TenantType.Platform, ct).ConfigureAwait(false))
        {
            db.IdentityTenants.Add(new Tenant
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Type = TenantType.Platform,
                DisplayName = "Platform",
                Locale = "ar",
                Status = TenantStatus.Active,
                Metadata = "{}",
                CreatedAt = DateTime.UtcNow,
            });
        }
        // Seed the 8 system roles used by Phase 9.
        var seed = new (string Name, RoleScope Scope, string Description)[]
        {
            ("super-admin", RoleScope.Platform, "Platform owner."),
            ("platform-operator", RoleScope.Platform, "Operations."),
            ("curriculum-admin", RoleScope.Platform, "Content admin."),
            ("subject-expert", RoleScope.Platform, "Content reviewer."),
            ("school-admin", RoleScope.School, "School admin."),
            ("teacher", RoleScope.School, "School staff."),
            ("parent", RoleScope.Family, "Family account holder."),
            ("student", RoleScope.Family, "Learner."),
        };
        foreach (var s in seed)
        {
            if (!await db.IdentityRoles.AnyAsync(r => r.Name == s.Name, ct).ConfigureAwait(false))
            {
                db.IdentityRoles.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = s.Name,
                    Scope = s.Scope,
                    Description = s.Description,
                    IsSystem = true,
                    CreatedAt = DateTime.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => Db.Dispose();

    /// <summary>
    /// Seeds a verified Family-tenant parent directly via DB inserts,
    /// bypassing the post-Paymob 2-phase registration flow (which creates
    /// the user only after payment confirmation). Used by every test
    /// that previously relied on <c>AuthService.RegisterParentAsync</c>
    /// returning a created user.
    /// </summary>
    public async Task<(Guid UserId, Guid TenantId)> SeedVerifiedParentAsync(
        string email,
        string password = "HorseBatteryStaple!77",
        CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();

        // Family tenant for this parent.
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Type = TenantType.Family,
            DisplayName = email,
            Locale = "ar",
            Status = TenantStatus.Active,
            Metadata = "{}",
            CreatedAt = DateTime.UtcNow,
        };
        Db.IdentityTenants.Add(tenant);

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            TenantId = tenant.Id,
            AccountType = AccountType.Personal,
            Email = email.Trim(),
            NormalizedEmail = normalized,
            FullName = "الوالد " + email,
            Locale = "ar",
            Status = UserStatus.Active,
            PasswordHash = Passwords.Hash(password),
            PasswordChangedAt = DateTime.UtcNow,
            EmailVerified = true,
            EmailVerifiedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        Db.IdentityUsers.Add(user);

        var parentRole = await Db.IdentityRoles.IgnoreQueryFilters()
            .SingleAsync(r => r.Name == "parent", ct).ConfigureAwait(false);
        Db.IdentityUserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = parentRole.Id,
            TenantId = tenant.Id,
            GrantedBy = userId,
            GrantedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (userId, tenant.Id);
    }

    /// <summary>
    /// Seeds a Family-tenant parent in <see cref="UserStatus.PendingEmailVerification"/>
    /// — used by tests that exercise the email-verification flow itself
    /// (where <see cref="IAuthService.RegisterParentAsync"/> no longer
    /// creates a User synchronously due to the Paymob 2-phase pattern).
    /// </summary>
    public async Task<(Guid UserId, Guid TenantId)> SeedUnverifiedParentAsync(
        string email,
        string password = "HorseBatteryStaple!77",
        CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Type = TenantType.Family,
            DisplayName = email,
            Locale = "ar",
            Status = TenantStatus.Active,
            Metadata = "{}",
            CreatedAt = DateTime.UtcNow,
        };
        Db.IdentityTenants.Add(tenant);

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            TenantId = tenant.Id,
            AccountType = AccountType.Personal,
            Email = email.Trim(),
            NormalizedEmail = normalized,
            FullName = "الوالد " + email,
            Locale = "ar",
            Status = UserStatus.PendingEmailVerification,
            PasswordHash = Passwords.Hash(password),
            PasswordChangedAt = DateTime.UtcNow,
            EmailVerified = false,
            CreatedAt = DateTime.UtcNow,
        };
        Db.IdentityUsers.Add(user);

        var parentRole = await Db.IdentityRoles.IgnoreQueryFilters()
            .SingleAsync(r => r.Name == "parent", ct).ConfigureAwait(false);
        Db.IdentityUserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = parentRole.Id,
            TenantId = tenant.Id,
            GrantedBy = userId,
            GrantedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (userId, tenant.Id);
    }
}

/// <summary>
/// Captures every audit event the service emits. Implemented as a
/// subclass because <see cref="AuditEventEmitter.Emit"/> is currently
/// non-virtual; the subclass wraps the base call and appends to a list
/// so tests can assert on specific actions.
/// </summary>
public sealed class CapturingAuditEventEmitter : AuditEventEmitter
{
    public List<AuditEvent> Events { get; } = new();

    public CapturingAuditEventEmitter() : base(NullLogger<AuditEventEmitter>.Instance) { }

    public override void Emit(AuditEvent auditEvent)
    {
        Events.Add(auditEvent);
        base.Emit(auditEvent);
    }
}

public sealed class AuditEventSpy
{
    public CapturingAuditEventEmitter Emitter { get; } = new();
    public IReadOnlyList<AuditEvent> Events => Emitter.Events;
}

/// <summary>
/// In-memory stand-in for <see cref="IIdentityNotificationSender"/>.
/// Stores every dispatch so tests can assert that a verification email
/// was triggered and capture the verification link it carried.
/// </summary>
public sealed class InMemoryNotificationSpy : IIdentityNotificationSender
{
    public List<IdentityNotificationRecord> Dispatched { get; } = new();

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendEmailVerificationAsync(
        IdentityNotificationRecipient recipient, string verificationLink, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord(
            "email_verification", recipient, verificationLink, correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendPasswordResetAsync(
        IdentityNotificationRecipient recipient, string resetLink, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("password_reset", recipient, resetLink, correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendPasswordChangedAsync(
        IdentityNotificationRecipient recipient, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("password_changed", recipient, string.Empty, correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendInvitationAsync(
        IdentityNotificationRecipient recipient, string role, string invitationLink, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord($"invitation:{role}", recipient, invitationLink, correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendUnusualLoginAsync(
        IdentityNotificationRecipient recipient, string device, string location, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("unusual_login", recipient, $"{device}|{location}", correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendChildCreatedAsync(
        IdentityNotificationRecipient recipient, string childName, string username, string tempPassword, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("child_created", recipient, $"{childName}:{username}", correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendChildUnusualLoginAsync(
        IdentityNotificationRecipient parentRecipient, string childName, string device, string location, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("child_unusual_login", parentRecipient, $"{childName}|{device}|{location}", correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendChildPasswordChangedByChildAsync(
        IdentityNotificationRecipient parentRecipient, string childName, string childGrade, string childUsername, DateTime changeTimeUtc, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("child_password_changed_by_child", parentRecipient, $"{childName}|{childGrade}|{childUsername}|{changeTimeUtc:O}", correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendChildBirthdayPinEligibleAsync(
        IdentityNotificationRecipient parentRecipient, string childName, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("child_birthday_pin_eligible", parentRecipient, childName, correlationId));
        return Task.FromResult(Receipt());
    }

    public Task<Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt> SendChildBirthdayPasswordEligibleAsync(
        IdentityNotificationRecipient parentRecipient, string childName, string correlationId, CancellationToken ct = default)
    {
        Dispatched.Add(new IdentityNotificationRecord("child_birthday_password_eligible", parentRecipient, childName, correlationId));
        return Task.FromResult(Receipt());
    }

    private static Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt Receipt()
        => new(Guid.NewGuid().ToString("D"), "email");
}

public sealed record IdentityNotificationRecord(
    string Kind,
    IdentityNotificationRecipient Recipient,
    string Link,
    string CorrelationId);

/// <summary>
/// In-memory stand-in for <see cref="ICredentialAuditWriter"/>. Captures
/// every credential audit event so tests can assert on the kinds emitted
/// (e.g. <c>child_password_changed_self</c>, rejection reasons) without
/// needing the real Phase 6 <c>AuditTrailWriter</c> + DB.
/// </summary>
public sealed class InMemoryCredentialAuditWriter : ICredentialAuditWriter
{
    public List<CredentialAuditEvent> Events { get; } = new();

    public Task WriteAsync(CredentialAuditEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test stub for <see cref="IManagerReAuthService"/>. By default
/// <c>HasRecentReAuthAsync</c> returns true so existing tests that
/// were written before re-auth landed continue to pass without
/// pre-stamping a receipt. Tests that exercise the re-auth gate
/// directly can flip <see cref="HasRecentResult"/>.
/// </summary>
public sealed class AlwaysFreshManagerReAuth : IManagerReAuthService
{
    public bool HasRecentResult { get; set; } = true;
    public ManagerReAuthOutcome NextVerifyOutcome { get; set; } = ManagerReAuthOutcome.Success;
    public List<Guid> Invalidations { get; } = new();

    public Task<bool> HasRecentReAuthAsync(Guid managerUserId, CancellationToken ct = default)
        => Task.FromResult(HasRecentResult);

    public Task<ManagerReAuthOutcome> VerifyAsync(Guid managerUserId, string password, string? totpCode, CancellationToken ct = default)
        => Task.FromResult(NextVerifyOutcome);

    public Task InvalidateAsync(Guid managerUserId, CancellationToken ct = default)
    {
        Invalidations.Add(managerUserId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test stub for <see cref="Muallimi.Api.Identity.Credentials.IChildCredentialNotifier"/>.
/// Captures every fan-out call so tests can assert on (kind, child)
/// without a real ParentNotification table or SMTP.
/// </summary>
public sealed class CapturingChildCredentialNotifier : Muallimi.Api.Identity.Credentials.IChildCredentialNotifier
{
    public List<(string Kind, Guid ChildId, string CorrelationId)> Fired { get; } = new();

    public Task NotifyChildPasswordChangedAsync(Muallimi.Domain.Identity.Entities.User child, string correlationId, CancellationToken ct = default)
    {
        Fired.Add(("child_password_changed", child.Id, correlationId));
        return Task.CompletedTask;
    }

    public Task NotifyBirthdayPinEligibleAsync(Muallimi.Domain.Identity.Entities.User child, string correlationId, CancellationToken ct = default)
    {
        Fired.Add(("child_birthday_pin_eligible", child.Id, correlationId));
        return Task.CompletedTask;
    }

    public Task NotifyBirthdayPasswordEligibleAsync(Muallimi.Domain.Identity.Entities.User child, string correlationId, CancellationToken ct = default)
    {
        Fired.Add(("child_birthday_password_eligible", child.Id, correlationId));
        return Task.CompletedTask;
    }
}
