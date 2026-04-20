using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T065 — Contract test for <c>POST /api/auth/refresh</c>. Pins:
///   • route constant;
///   • <see cref="RefreshRequest"/> wire shape;
///   • rotate-on-use behaviour — the old token is revoked and the
///     response carries a different plaintext + a valid access token;
///   • reuse detection — a second refresh with the original token
///     triggers reuse detection, revokes the family, and blocks future
///     refresh attempts.
/// </summary>
public class RefreshContractTests
{
    [Fact]
    public void Route_Is_Pinned()
    {
        Assert.Equal("/refresh", PublicAuthEndpoints.RefreshRoute);
    }

    [Fact]
    public void Refresh_Request_Shape_Is_CamelCase()
    {
        var names = typeof(RefreshRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
        Assert.Contains("refreshToken", names);
    }

    [Fact]
    public async Task Refresh_Rotates_Token_And_Issues_New_Access()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await LoginContractTests.RegisterAndVerifyAsync(h, "rot@example.com", "HorseBatteryStaple!77");

        var login = await h.AuthService.LoginAsync(LoginCmd("rot@example.com", "HorseBatteryStaple!77"));
        Assert.True(login.Success);
        var originalRefresh = login.Payload!.RefreshToken;
        Assert.False(string.IsNullOrWhiteSpace(originalRefresh));

        var refreshed = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            originalRefresh, "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));

        Assert.True(refreshed.Success);
        Assert.NotEqual(originalRefresh, refreshed.Payload!.RefreshToken);
        Assert.NotEqual(login.Payload.AccessToken, refreshed.Payload.AccessToken);
        Assert.Contains(h.Audit.Events, e => e.Action == "refresh" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task Reusing_Old_Refresh_Token_Triggers_Family_Revocation()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await LoginContractTests.RegisterAndVerifyAsync(h, "reuse@example.com", "HorseBatteryStaple!77");

        var login = await h.AuthService.LoginAsync(LoginCmd("reuse@example.com", "HorseBatteryStaple!77"));
        var originalRefresh = login.Payload!.RefreshToken;

        // First rotation — legitimate.
        var ok = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            originalRefresh, "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.True(ok.Success);

        // Replay of the ORIGINAL token — reuse detection must fire.
        var reuse = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            originalRefresh, "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.False(reuse.Success);
        Assert.Equal(401, reuse.HttpStatus);
        Assert.Equal("refresh_token_reused", reuse.ErrorCode);

        Assert.Contains(h.Audit.Events, e => e.Action == "refresh_reuse_detected" && e.Outcome == "blocked");

        // The new token issued in the legitimate refresh is also dead
        // (whole family revoked).
        var deadPath = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            ok.Payload!.RefreshToken, "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.False(deadPath.Success);
    }

    [Fact]
    public async Task Unknown_Refresh_Token_Returns_InvalidRefreshToken()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var outcome = await h.AuthService.RefreshAsync(new RefreshTokenCommand(
            "never-issued-token", "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.False(outcome.Success);
        Assert.Equal(401, outcome.HttpStatus);
        Assert.Equal("invalid_refresh_token", outcome.ErrorCode);
    }

    private static LoginCommand LoginCmd(string email, string password) => new(
        email, password, RememberMe: false, TwoFactorCode: null, TempToken: null,
        IpAddress: "127.0.0.1", UserAgent: "xunit", CorrelationId: Guid.NewGuid().ToString("D"));
}
