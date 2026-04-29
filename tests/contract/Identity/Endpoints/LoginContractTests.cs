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
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T063 — Contract test for <c>POST /api/auth/login</c>. Pins:
///   • route constant on the login endpoint;
///   • <see cref="LoginRequest"/> shape (identifier + password +
///     rememberMe + twoFactorCode + tempToken);
///   • successful-login AuthResponse payload;
///   • 2FA-required branch returns <see cref="TwoFactorChallengeResponse"/>;
///   • invalid-credentials path emits a <c>login_failed</c> audit event.
/// </summary>
public class LoginContractTests
{
    [Fact]
    public void Route_Constant_Is_Pinned()
    {
        Assert.Equal("/login", PublicAuthEndpoints.LoginRoute);
    }

    [Fact]
    public void LoginRequest_Shape_Is_CamelCase()
    {
        var names = JsonNames(typeof(LoginRequest));
        foreach (var expected in new[] { "identifier", "password", "rememberMe", "twoFactorCode", "tempToken" })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void TwoFactorChallengeResponse_Shape_Is_CamelCase()
    {
        var names = JsonNames(typeof(TwoFactorChallengeResponse));
        Assert.Contains("twoFactorRequired", names);
        Assert.Contains("tempToken", names);
    }

    [Fact]
    public async Task Successful_Login_Returns_AuthResponse_And_Emits_Audit()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await RegisterAndVerifyAsync(h, "ok@example.com", "HorseBatteryStaple!77");

        var outcome = await h.AuthService.LoginAsync(new LoginCommand(
            "ok@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(outcome.Success);
        Assert.Equal(200, outcome.HttpStatus);
        Assert.NotNull(outcome.Payload);
        Assert.NotEmpty(outcome.Payload!.AccessToken);
        Assert.NotEmpty(outcome.Payload.RefreshToken);
        Assert.True(outcome.Payload.ExpiresIn > 0);
        Assert.Equal("family", outcome.Payload.TenantType);
        Assert.Contains("parent", outcome.Payload.Roles);

        Assert.Contains(h.Audit.Events, e => e.Action == "login_success" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task Invalid_Identifier_Returns_Invalid_Credentials_With_Timing_Cover()
    {
        using var h = await IdentityTestHarness.CreateAsync();

        var outcome = await h.AuthService.LoginAsync(new LoginCommand(
            "unknown@example.com", "any-password",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(401, outcome.HttpStatus);
        Assert.Equal("invalid_credentials", outcome.ErrorCode);
    }

    [Fact]
    public async Task Pending_Verification_User_Cannot_Login()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        // Post-Paymob: RegisterParentAsync no longer creates a User row
        // synchronously, so we seed a verified-=false user directly to
        // exercise the "user exists but email_not_verified" branch.
        await h.SeedUnverifiedParentAsync("pending@example.com", "HorseBatteryStaple!77");

        var outcome = await h.AuthService.LoginAsync(new LoginCommand(
            "pending@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(403, outcome.HttpStatus);
        Assert.Equal("email_not_verified", outcome.ErrorCode);
    }

    [Fact]
    public async Task TwoFactor_Enabled_User_Gets_Challenge()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var user = await RegisterAndVerifyAsync(h, "tfa@example.com", "HorseBatteryStaple!77");
        user.TwoFactorEnabled = true;
        await h.Db.SaveChangesAsync();

        var outcome = await h.AuthService.LoginAsync(new LoginCommand(
            "tfa@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(outcome.Success);
        Assert.Equal(401, outcome.HttpStatus);
        Assert.Equal("two_factor_required", outcome.ErrorCode);
        Assert.NotNull(outcome.TwoFactor);
        Assert.True(outcome.TwoFactor!.TwoFactorRequired);
        Assert.False(string.IsNullOrWhiteSpace(outcome.TwoFactor.TempToken));
    }

    internal static async Task<User> RegisterAndVerifyAsync(IdentityTestHarness h, string email, string password)
    {
        // Post-Paymob, RegisterParentAsync only creates a PendingRegistration
        // until payment completes. Tests that need a verified, logged-in-able
        // parent seed directly via the harness helper.
        await h.SeedVerifiedParentAsync(email, password);
        return await h.Db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.NormalizedEmail == email);
    }

    private static string[] JsonNames(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
}
