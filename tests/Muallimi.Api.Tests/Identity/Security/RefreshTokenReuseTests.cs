using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Xunit;

namespace Muallimi.Api.Tests.Identity.Security;

/// <summary>
/// T068 — Security test: replaying a refresh token that has already
/// rotated triggers reuse detection, revokes the entire session family
/// (every refresh token tied to the same <c>SessionId</c>), and
/// completes within 100 ms on local dev hardware.
/// </summary>
public class RefreshTokenReuseTests
{
    [Fact]
    public async Task Reused_Refresh_Token_Revokes_Family_Within_100ms()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await Muallimi.MainBackend.Tests.Contract.Identity.Endpoints.LoginContractTests
            .RegisterAndVerifyAsync(h, "reuse-sec@example.com", "HorseBatteryStaple!77");

        var login = await h.AuthService.LoginAsync(new LoginCommand(
            "reuse-sec@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        var original = login.Payload!.RefreshToken;

        // Legitimate rotation first.
        var rotated = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            original, "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.True(rotated.Success);

        // Replay. Measure the reuse-detection path.
        var sw = Stopwatch.StartNew();
        var reuse = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            original, "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        sw.Stop();

        Assert.False(reuse.Success);
        Assert.Equal("refresh_token_reused", reuse.ErrorCode);
        Assert.True(sw.ElapsedMilliseconds < 100,
            $"Reuse detection took {sw.ElapsedMilliseconds}ms (budget 100ms).");

        Assert.Contains(h.Audit.Events, e => e.Action == "refresh_reuse_detected" && e.Outcome == "blocked");

        // Every refresh token in the session family is revoked.
        var tokens = await h.Db.IdentityRefreshTokens.IgnoreQueryFilters().ToListAsync();
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task Legitimate_Rotation_Does_Not_Trigger_Reuse_Detection()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await Muallimi.MainBackend.Tests.Contract.Identity.Endpoints.LoginContractTests
            .RegisterAndVerifyAsync(h, "ok-rot@example.com", "HorseBatteryStaple!77");

        var login = await h.AuthService.LoginAsync(new LoginCommand(
            "ok-rot@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        var current = login.Payload!.RefreshToken;

        for (var i = 0; i < 3; i++)
        {
            var r = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
                current, "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
            Assert.True(r.Success);
            current = r.Payload!.RefreshToken;
        }

        Assert.DoesNotContain(h.Audit.Events, e => e.Action == "refresh_reuse_detected");
    }
}
