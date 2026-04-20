using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T141 — Contract test pinning the US5 parent-oversight routes, DTO shapes,
/// and core service behaviours:
///   • <c>POST /parent/children/{id}/suspend</c>
///   • <c>POST /parent/children/{id}/unsuspend</c>
///   • <c>GET  /parent/children/{id}/sessions</c>
///   • <c>DELETE /parent/children/{id}/sessions/{sessionId}</c>
///   • <c>GET  /parent/children/{id}/login-history</c>
/// </summary>
public class ParentOversightContractTests
{
    // ── Route constants ──────────────────────────────────────────────────

    [Fact]
    public void Suspend_And_Unsuspend_Route_Constants_Are_Pinned()
    {
        Assert.Equal("/{id:guid}/suspend", ParentChildrenEndpoints.SuspendSubRoute);
        Assert.Equal("/{id:guid}/unsuspend", ParentChildrenEndpoints.UnsuspendSubRoute);
    }

    [Fact]
    public void Sessions_And_LoginHistory_Route_Constants_Are_Pinned()
    {
        Assert.Equal("/{id:guid}/sessions", ParentChildrenEndpoints.SessionsSubRoute);
        Assert.Equal("/{id:guid}/login-history", ParentChildrenEndpoints.LoginHistorySubRoute);
    }

    // ── DTO shapes ───────────────────────────────────────────────────────

    [Fact]
    public void ChildSessionSummary_Shape_Is_CamelCase()
    {
        var names = JsonNames(typeof(ChildSessionSummary));
        foreach (var expected in new[]
        {
            "sessionId", "deviceName", "deviceType", "ipAddress",
            "userAgent", "createdAt", "lastSeenAt",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void ChildLoginHistoryItem_Shape_Is_CamelCase()
    {
        var names = JsonNames(typeof(ChildLoginHistoryItem));
        foreach (var expected in new[]
        {
            "id", "ipAddress", "userAgent", "outcome", "failureReason", "attemptedAt",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void ChildSessionSummary_Does_Not_Leak_Sensitive_Fields()
    {
        var names = JsonNames(typeof(ChildSessionSummary));
        Assert.DoesNotContain("passwordHash", names);
        Assert.DoesNotContain("tokenHash", names);
    }

    // ── Suspend / unsuspend behaviour ────────────────────────────────────

    [Fact]
    public async Task SuspendChild_Transitions_Status_To_Suspended_And_Audits()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, tenantId) = await RegisterAndVerifyParentAsync(h, "p-suspend@example.com");
        var svc = BuildService(h);

        var child = await CreateChildAsync(svc, parentId, tenantId, "معاذ");
        var childId = Guid.Parse(child.UserId);

        var result = await svc.SuspendChildAsync(new SuspendChildCommand(
            parentId, tenantId, childId, "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        Assert.True(result.Success);
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == childId);
        Assert.Equal(UserStatus.Suspended, user.Status);
        Assert.Contains(h.Audit.Events, e => e.Action == "child_suspended" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task UnsuspendChild_Restores_Active_Status_And_Audits()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, tenantId) = await RegisterAndVerifyParentAsync(h, "p-unsuspend@example.com");
        var svc = BuildService(h);

        var child = await CreateChildAsync(svc, parentId, tenantId, "لجين");
        var childId = Guid.Parse(child.UserId);

        await svc.SuspendChildAsync(new SuspendChildCommand(
            parentId, tenantId, childId, "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        var result = await svc.UnsuspendChildAsync(new UnsuspendChildCommand(
            parentId, tenantId, childId, "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        Assert.True(result.Success);
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == childId);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Contains(h.Audit.Events, e => e.Action == "child_unsuspended" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task Suspend_Returns_NotFound_For_Unowned_Child()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentA, tenantA) = await RegisterAndVerifyParentAsync(h, "pA-sus@example.com");
        var (parentB, tenantB) = await RegisterAndVerifyParentAsync(h, "pB-sus@example.com");
        var svc = BuildService(h);

        var child = await CreateChildAsync(svc, parentA, tenantA, "فريد");
        var childId = Guid.Parse(child.UserId);

        var result = await svc.SuspendChildAsync(new SuspendChildCommand(
            parentB, tenantB, childId, "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        Assert.False(result.Success);
        Assert.Equal(404, result.HttpStatus);
    }

    // ── Session list + revoke ────────────────────────────────────────────

    [Fact]
    public async Task RevokeChildSession_Removes_Session_And_Audits()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, tenantId) = await RegisterAndVerifyParentAsync(h, "p-revoke@example.com");
        var svc = BuildService(h);

        var child = await CreateChildAsync(svc, parentId, tenantId, "سيف");
        var childId = Guid.Parse(child.UserId);

        // Create a session directly.
        var session = await h.Sessions.CreateAsync(new CreateSessionInput(
            childId, "10.0.0.1", "TestBrowser", "Device", DeviceType.Unknown));

        var sessions = await svc.ListChildSessionsAsync(parentId, childId);
        Assert.Single(sessions);
        Assert.Equal(session.Id.ToString("D"), sessions[0].SessionId);

        var revoke = await svc.RevokeChildSessionAsync(new RevokeChildSessionCommand(
            parentId, tenantId, childId, session.Id,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        Assert.True(revoke.Success);
        var afterRevoke = await svc.ListChildSessionsAsync(parentId, childId);
        Assert.Empty(afterRevoke);
        Assert.Contains(h.Audit.Events, e => e.Action == "child_session_revoked" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task ListChildSessions_Returns_Empty_For_Unowned_Child()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentA, tenantA) = await RegisterAndVerifyParentAsync(h, "pA-sess@example.com");
        var (parentB, _) = await RegisterAndVerifyParentAsync(h, "pB-sess@example.com");
        var svc = BuildService(h);

        var child = await CreateChildAsync(svc, parentA, tenantA, "ريان");
        var childId = Guid.Parse(child.UserId);

        var sessions = await svc.ListChildSessionsAsync(parentB, childId);
        Assert.Empty(sessions);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static async Task<(Guid UserId, Guid TenantId)> RegisterAndVerifyParentAsync(
        IdentityTestHarness h, string email)
    {
        var outcome = await h.AuthService.RegisterParentAsync(new RegisterParentCommand(
            Email: email,
            Password: "HorseBatteryStaple!77",
            FullName: "ولي الأمر",
            FullNameEn: null,
            Locale: "ar",
            AcceptedTerms: true,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(outcome.Success);
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.NormalizedEmail == email.ToLowerInvariant());
        user.VerifyEmail();
        await h.Db.SaveChangesAsync();
        return (user.Id, user.TenantId);
    }

    private static async Task<ChildCredentialsOnce> CreateChildAsync(
        UserManagementService svc, Guid parentId, Guid tenantId, string name)
    {
        var result = await svc.CreateChildAsync(new CreateChildCommand(
            parentId, tenantId, name, null, 5, "male",
            new DateTime(2015, 1, 1), null, null, "ar",
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.True(result.Success);
        return result.Payload!;
    }

    private static UserManagementService BuildService(IdentityTestHarness h)
        => new(
            h.Db,
            h.Passwords,
            new UsernameGenerator(new Random(1)),
            new ChildPasswordGenerator(new Random(2)),
            h.Audit.Emitter,
            h.Notifications,
            NullLogger<UserManagementService>.Instance);

    private static string[] JsonNames(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
}
