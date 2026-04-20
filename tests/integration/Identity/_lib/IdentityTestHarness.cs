using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Audit;
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

        var auth = new AuthService(
            db, passwords, tokens, rateLimit, sessions, audit.Emitter,
            notifications, verification, linkBuilder,
            NullLogger<AuthService>.Instance,
            twoFactorMgmt);

        var resetLinkBuilder = new PasswordResetLinkBuilder("http://test.local");
        var pwReset = new PasswordResetService(
            db, passwords, sessions, audit.Emitter, notifications,
            resetLinkBuilder, NullLogger<PasswordResetService>.Instance);

        var impersonation = new Muallimi.Api.Identity.Services.ImpersonationService(
            db, tokens, audit.Emitter, NullLogger<Muallimi.Api.Identity.Services.ImpersonationService>.Instance);

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

    private static Muallimi.Application.Notifications.Channels.NotificationDispatchReceipt Receipt()
        => new(Guid.NewGuid().ToString("D"), "email");
}

public sealed record IdentityNotificationRecord(
    string Kind,
    IdentityNotificationRecipient Recipient,
    string Link,
    string CorrelationId);
