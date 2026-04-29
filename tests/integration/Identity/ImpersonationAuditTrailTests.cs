using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.Api.Tests.Integration.Identity;

/// <summary>
/// T152 — Integration tests for operator impersonation with audit trail.
///
/// Verifies:
///   1. Start impersonation emits <c>impersonation_started</c> audit event
///      with mandatory Reason and ImpersonationSessionId.
///   2. Every audit event emitted within the session carries
///      <c>ImpersonationSessionId</c> (contract requirement).
///   3. End impersonation emits <c>impersonation_ended</c> and marks the
///      session <c>EndedAt</c>.
///   4. Expiry sweep marks expired sessions and emits
///      <c>impersonation_expired</c>.
///   5. Target-not-found returns a 404 outcome.
///   6. Non-session-owner cannot end a session (403).
/// </summary>
public class ImpersonationAuditTrailTests
{
    [Fact]
    public async Task Start_Impersonation_Persists_Session_And_Emits_Audit()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);
        var (targetId, _) = await RegisterAndVerifyParentAsync(h, "target-audit@example.com");

        var corr = Guid.NewGuid().ToString("D");
        var outcome = await h.ImpersonationService.StartAsync(new StartImpersonationCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            TargetUserId: targetId,
            Reason: "support investigation",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: corr));

        Assert.True(outcome.Success, outcome.Message);
        Assert.NotNull(outcome.Payload);

        // Session persisted.
        var session = await h.Db.IdentityImpersonationSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == Guid.Parse(outcome.Payload!.ImpersonationSessionId));
        Assert.NotNull(session);
        Assert.Null(session!.EndedAt);
        Assert.True(session.IsActive);
        Assert.Equal("support investigation", session.Reason);
        Assert.Equal(superAdminId, session.ImpersonatorId);
        Assert.Equal(targetId, session.TargetUserId);

        // Audit event emitted.
        var auditEvent = h.Audit.Events.FirstOrDefault(e => e.Action == "impersonation_started");
        Assert.NotNull(auditEvent);
        Assert.Equal("Impersonation", auditEvent!.EventCategory);
        Assert.Equal(superAdminId.ToString("D"), auditEvent.ActorId);
        Assert.Equal(targetId.ToString("D"), auditEvent.TargetId);
        Assert.Equal("support investigation", auditEvent.Reason);
        Assert.Equal(outcome.Payload!.ImpersonationSessionId, auditEvent.ImpersonationSessionId);
        Assert.Equal("succeeded", auditEvent.Outcome);
    }

    [Fact]
    public async Task Every_Audit_Event_During_Session_Carries_ImpersonationSessionId()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);
        var (targetId, _) = await RegisterAndVerifyParentAsync(h, "target-tag@example.com");

        var startOutcome = await h.ImpersonationService.StartAsync(new StartImpersonationCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            TargetUserId: targetId,
            Reason: "audit tag test",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(startOutcome.Success);
        var sessionId = startOutcome.Payload!.ImpersonationSessionId;

        // All impersonation-related audit events must have the session id.
        var impersonationEvents = h.Audit.Events
            .Where(e => e.EventCategory == "Impersonation")
            .ToList();
        Assert.All(impersonationEvents, e =>
            Assert.Equal(sessionId, e.ImpersonationSessionId));
    }

    [Fact]
    public async Task End_Impersonation_Marks_Session_And_Emits_Audit()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);
        var (targetId, _) = await RegisterAndVerifyParentAsync(h, "target-end@example.com");

        var startOutcome = await h.ImpersonationService.StartAsync(new StartImpersonationCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            TargetUserId: targetId,
            Reason: "end test",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(startOutcome.Success);
        var sessionId = Guid.Parse(startOutcome.Payload!.ImpersonationSessionId);

        var endOutcome = await h.ImpersonationService.EndAsync(new EndImpersonationCommand(
            ActorUserId: superAdminId,
            ImpersonationSessionId: sessionId,
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(endOutcome.Success, endOutcome.Message);

        // Session marked ended.
        var session = await h.Db.IdentityImpersonationSessions.IgnoreQueryFilters()
            .FirstAsync(s => s.Id == sessionId);
        Assert.NotNull(session.EndedAt);
        Assert.False(session.IsActive);

        // Audit event.
        var endAudit = h.Audit.Events.FirstOrDefault(e => e.Action == "impersonation_ended");
        Assert.NotNull(endAudit);
        Assert.Equal(superAdminId.ToString("D"), endAudit!.ActorId);
        Assert.Equal(sessionId.ToString("D"), endAudit.ImpersonationSessionId);
        Assert.Equal("succeeded", endAudit.Outcome);
    }

    [Fact]
    public async Task Expiry_Sweep_Marks_Expired_Sessions_And_Emits_Impersonation_Expired()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);
        var (targetId, targetTenantId) = await RegisterAndVerifyParentAsync(h, "target-expire@example.com");

        // Manually insert an already-expired session.
        var expiredSession = new Muallimi.Domain.Identity.Entities.ImpersonationSession
        {
            Id = Guid.NewGuid(),
            ImpersonatorId = superAdminId,
            TargetUserId = targetId,
            TargetTenantId = targetTenantId,
            Reason = "already expired",
            StartedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddHours(-1),
            CorrelationId = Guid.NewGuid().ToString("D"),
        };
        h.Db.IdentityImpersonationSessions.Add(expiredSession);
        await h.Db.SaveChangesAsync();

        var eventCountBefore = h.Audit.Events.Count;

        await h.ImpersonationService.ExpireStaleSessionsAsync();

        var session = await h.Db.IdentityImpersonationSessions.IgnoreQueryFilters()
            .FirstAsync(s => s.Id == expiredSession.Id);
        Assert.NotNull(session.EndedAt);

        var expiredAudit = h.Audit.Events.Skip(eventCountBefore).FirstOrDefault(e => e.Action == "impersonation_expired");
        Assert.NotNull(expiredAudit);
        Assert.Equal("Impersonation", expiredAudit!.EventCategory);
        Assert.Equal(expiredSession.Id.ToString("D"), expiredAudit.ImpersonationSessionId);
    }

    [Fact]
    public async Task Start_With_Unknown_Target_Returns_404()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);

        var outcome = await h.ImpersonationService.StartAsync(new StartImpersonationCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            TargetUserId: Guid.NewGuid(),
            Reason: "test",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(404, outcome.HttpStatus);
        Assert.Equal("target_not_found", outcome.ErrorCode);
    }

    [Fact]
    public async Task Start_Without_Reason_Returns_400()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);
        var (targetId, _) = await RegisterAndVerifyParentAsync(h, "target-noreason@example.com");

        var outcome = await h.ImpersonationService.StartAsync(new StartImpersonationCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            TargetUserId: targetId,
            Reason: "",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(400, outcome.HttpStatus);
        Assert.Equal("reason_required", outcome.ErrorCode);
    }

    [Fact]
    public async Task Non_Owner_Cannot_End_Another_Admins_Session()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);
        var (targetId, _) = await RegisterAndVerifyParentAsync(h, "target-ownership@example.com");

        var startOutcome = await h.ImpersonationService.StartAsync(new StartImpersonationCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            TargetUserId: targetId,
            Reason: "ownership test",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(startOutcome.Success);
        var sessionId = Guid.Parse(startOutcome.Payload!.ImpersonationSessionId);

        // A different actor tries to end the session.
        var differentActor = Guid.NewGuid();
        var endOutcome = await h.ImpersonationService.EndAsync(new EndImpersonationCommand(
            ActorUserId: differentActor,
            ImpersonationSessionId: sessionId,
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(endOutcome.Success);
        Assert.Equal(403, endOutcome.HttpStatus);
        Assert.Equal("not_session_owner", endOutcome.ErrorCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Task<(Guid UserId, Guid TenantId)> RegisterAndVerifyParentAsync(
        IdentityTestHarness h, string email)
        => h.SeedVerifiedParentAsync(email);

    private static async Task<(Guid superAdminId, Guid platformTenantId)> SetupSuperAdminAsync(
        IdentityTestHarness h)
    {
        var platformTenant = await h.Db.IdentityTenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Type == TenantType.Platform);
        var superAdminRole = await h.Db.IdentityRoles.IgnoreQueryFilters()
            .FirstAsync(r => r.Name == "super-admin");

        var adminSuffix = Guid.NewGuid().ToString("N");
        var superAdmin = new Muallimi.Domain.Identity.Entities.User
        {
            Id = Guid.NewGuid(),
            TenantId = platformTenant.Id,
            Email = $"superadmin-{adminSuffix}@platform.io",
            NormalizedEmail = $"SUPERADMIN-{adminSuffix.ToUpperInvariant()}@PLATFORM.IO",
            FullName = "Super Admin",
            Locale = "ar",
            Status = Muallimi.Domain.Identity.Enums.UserStatus.Active,
            AccountType = Muallimi.Domain.Identity.Enums.AccountType.Personal,
            PasswordHash = h.Passwords.Hash("SuperAdmin!77"),
            EmailVerified = true,
            CreatedAt = DateTime.UtcNow,
        };
        h.Db.IdentityUsers.Add(superAdmin);
        h.Db.IdentityUserRoles.Add(new Muallimi.Domain.Identity.Entities.UserRole
        {
            Id = Guid.NewGuid(),
            UserId = superAdmin.Id,
            RoleId = superAdminRole.Id,
            TenantId = platformTenant.Id,
            GrantedAt = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();

        return (superAdmin.Id, platformTenant.Id);
    }
}
