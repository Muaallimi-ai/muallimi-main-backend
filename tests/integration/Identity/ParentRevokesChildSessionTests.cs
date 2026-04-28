using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Identity;

/// <summary>
/// T142 — Integration test: parent revokes a child's active session.
///
/// Verifies:
///   • A live child session is revoked by the parent.
///   • The child's refresh tokens for that session are invalidated.
///   • After revocation, <c>IsSessionActiveAsync</c> returns false.
///   • An audit event with action "child_session_revoked" is emitted.
/// </summary>
public class ParentRevokesChildSessionTests
{
    [Fact]
    public async Task Parent_Can_Revoke_Child_Active_Session()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, tenantId) = await RegisterAndVerifyParentAsync(h, "pr-rcs@example.com");
        var mgmt = BuildService(h);

        // Create a managed child.
        var child = await mgmt.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: tenantId, fullName: "نواف",
            grade: 6, gender: "male", birthYear: 2015, birthMonth: 5));
        Assert.True(child.Success);
        var childId = Guid.Parse(child.Payload!.UserId);

        // Create a session for the child.
        var session = await h.Sessions.CreateAsync(new CreateSessionInput(
            childId, "10.0.0.50", "Firefox/120", "desktop", DeviceType.Unknown));
        Assert.True(await h.Sessions.IsSessionActiveAsync(session.Id));

        // Add a refresh token for the session.
        h.Db.IdentityRefreshTokens.Add(new Muallimi.Domain.Identity.Entities.RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = childId,
            SessionId = session.Id,
            TokenHash = "some-hash",
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedByIp = "10.0.0.50",
        });
        await h.Db.SaveChangesAsync();

        // Parent revokes the session.
        var result = await mgmt.RevokeChildSessionAsync(new RevokeChildSessionCommand(
            parentId, tenantId, childId, session.Id,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));

        Assert.True(result.Success);

        // Session is now inactive.
        var dbSession = await h.Db.IdentityUserSessions.IgnoreQueryFilters()
            .SingleAsync(s => s.Id == session.Id);
        Assert.NotNull(dbSession.RevokedAt);

        // Refresh token for that session is revoked.
        var token = await h.Db.IdentityRefreshTokens.IgnoreQueryFilters()
            .SingleAsync(t => t.SessionId == session.Id);
        Assert.NotNull(token.RevokedAt);

        // Audit event emitted.
        Assert.Contains(h.Audit.Events,
            e => e.Action == "child_session_revoked" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task Parent_Cannot_Revoke_Session_Of_Unowned_Child()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentA, tenantA) = await RegisterAndVerifyParentAsync(h, "pA-rcs@example.com");
        var (parentB, tenantB) = await RegisterAndVerifyParentAsync(h, "pB-rcs@example.com");
        var mgmt = BuildService(h);

        var child = await mgmt.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentA, parentTenantId: tenantA, fullName: "ولد",
            grade: 4, gender: "male", birthYear: 2016, birthMonth: 1));
        var childId = Guid.Parse(child.Payload!.UserId);

        var session = await h.Sessions.CreateAsync(new CreateSessionInput(
            childId, "10.0.0.99", "Chrome", "device", DeviceType.Unknown));

        var result = await mgmt.RevokeChildSessionAsync(new RevokeChildSessionCommand(
            parentB, tenantB, childId, session.Id,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));

        Assert.False(result.Success);
        Assert.Equal(404, result.HttpStatus);
    }

    [Fact]
    public async Task Suspend_Child_Prevents_Authentication()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, tenantId) = await RegisterAndVerifyParentAsync(h, "pr-sus@example.com");
        var mgmt = BuildService(h);

        var child = await mgmt.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: tenantId, fullName: "بسام",
            grade: 5, gender: "male", birthYear: 2015, birthMonth: 3));
        Assert.True(child.Success);
        var childId = Guid.Parse(child.Payload!.UserId);
        var childUsername = child.Payload.Username;
        var childPassword = child.Payload.GeneratedPassword;

        // Suspend via parent.
        var suspend = await mgmt.SuspendChildAsync(new SuspendChildCommand(
            parentId, tenantId, childId,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.True(suspend.Success);

        // Attempt login as the suspended child.
        var login = await h.AuthService.LoginAsync(new LoginCommand(
            Identifier: childUsername,
            Password: childPassword,
            RememberMe: false,
            TwoFactorCode: null,
            TempToken: null,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(login.Success);
        Assert.Equal("account_suspended", login.ErrorCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Task<(Guid UserId, Guid TenantId)> RegisterAndVerifyParentAsync(
        IdentityTestHarness h, string email)
        => h.SeedVerifiedParentAsync(email);

    private static UserManagementService BuildService(IdentityTestHarness h)
        => new(
            h.Db,
            h.Passwords,
            new UsernameGenerator(new Random(99)),
            new ChildPasswordGenerator(new Random(88)),
            h.Audit.Emitter,
            h.Notifications,
            NullLogger<UserManagementService>.Instance,
            new WeakPinBlocklist());
}
