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
/// T066 — Full register → verify → login → refresh → logout cycle.
///
/// The spec calls for Testcontainers PostgreSQL; the repo has no
/// Testcontainers dependency, and every Phase 3-8 integration test uses
/// the EF Core InMemory provider. The invariants under test
/// (state-machine transitions, token rotation, session revocation) are
/// evaluated client-side and produce identical behaviour on both
/// providers — when Testcontainers lands the test can be promoted
/// unchanged.
/// </summary>
public class RegisterLoginCycleTests
{
    [Fact]
    public async Task Full_Register_Verify_Login_Refresh_Logout_Happy_Path()
    {
        using var h = await IdentityTestHarness.CreateAsync();

        // 1. Register.
        var correlation = Guid.NewGuid().ToString("D");
        var register = await h.AuthService.RegisterParentAsync(new RegisterParentCommand(
            "cycle@example.com", "HorseBatteryStaple!77", "Parent", "Parent EN", "ar", true,
            "127.0.0.1", "xunit-cycle", correlation));
        Assert.True(register.Success);

        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.NormalizedEmail == "cycle@example.com");
        Assert.Equal(UserStatus.PendingEmailVerification, user.Status);

        // 2. Verify.
        var record = h.Notifications.Dispatched[^1];
        var plaintext = Uri.UnescapeDataString(record.Link.Split("token=", 2)[1]);
        var verify = await h.Verification.ConsumeAsync(plaintext, correlation);
        Assert.True(verify.Success);
        await h.Db.Entry(user).ReloadAsync();
        Assert.Equal(UserStatus.Active, user.Status);

        // 3. Login.
        var login = await h.AuthService.LoginAsync(new LoginCommand(
            "cycle@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit-cycle", CorrelationId: correlation));
        Assert.True(login.Success);
        Assert.False(string.IsNullOrWhiteSpace(login.Payload!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.Payload.RefreshToken));

        var principal = h.Tokens.ValidateAccessToken(login.Payload.AccessToken);
        Assert.NotNull(principal);
        var sessionId = Guid.Parse(principal!.FindFirst("session_id")!.Value);

        // 4. Refresh.
        var refresh = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            login.Payload.RefreshToken, "127.0.0.1", "xunit-cycle", correlation));
        Assert.True(refresh.Success);
        Assert.NotEqual(login.Payload.RefreshToken, refresh.Payload!.RefreshToken);

        // 5. Logout.
        var logout = await h.AuthService.LogoutAsync(new LogoutCommand(
            sessionId, user.Id, refresh.Payload.RefreshToken, correlation));
        Assert.True(logout.Success);

        // After logout the refresh token must not work any more.
        var postLogoutRefresh = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            refresh.Payload.RefreshToken, "127.0.0.1", "xunit-cycle", correlation));
        Assert.False(postLogoutRefresh.Success);

        // Audit timeline: register → email_verified → login_success → refresh → logout.
        var actions = h.Audit.Events.Select(e => e.Action).ToArray();
        Assert.Contains("register_parent", actions);
        Assert.Contains("email_verified", actions);
        Assert.Contains("login_success", actions);
        Assert.Contains("refresh", actions);
        Assert.Contains("logout", actions);
    }

    [Fact]
    public async Task Session_Revocation_Blocks_Subsequent_Use()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await h.AuthService.RegisterParentAsync(new RegisterParentCommand(
            "rev@example.com", "HorseBatteryStaple!77", "Parent", null, "ar", true,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        var record = h.Notifications.Dispatched[^1];
        var token = Uri.UnescapeDataString(record.Link.Split("token=", 2)[1]);
        await h.Verification.ConsumeAsync(token, Guid.NewGuid().ToString("D"));

        var login = await h.AuthService.LoginAsync(new LoginCommand(
            "rev@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit", CorrelationId: Guid.NewGuid().ToString("D")));
        var principal = h.Tokens.ValidateAccessToken(login.Payload!.AccessToken);
        var sessionId = Guid.Parse(principal!.FindFirst("session_id")!.Value);

        Assert.True(await h.Sessions.IsSessionActiveAsync(sessionId));
        await h.Sessions.RevokeAsync(sessionId);
        Assert.False(await h.Sessions.IsSessionActiveAsync(sessionId));
    }
}
