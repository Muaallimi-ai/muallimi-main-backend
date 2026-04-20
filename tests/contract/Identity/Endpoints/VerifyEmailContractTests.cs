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
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T064 — Contract test for <c>POST /api/auth/verify-email</c> and
/// <c>/resend-verification</c>.
///
/// Covers:
///   • route constants;
///   • wire shapes of <see cref="VerifyEmailRequest"/> +
///     <see cref="ResendVerificationRequest"/>;
///   • token consumption transitions the user to
///     <see cref="UserStatus.Active"/> and emits <c>email_verified</c>;
///   • invalid / expired tokens produce <c>token_invalid</c>;
///   • resend invalidates outstanding tokens and issues a fresh one
///     that the notification spy captures.
/// </summary>
public class VerifyEmailContractTests
{
    [Fact]
    public void Routes_Are_Pinned()
    {
        Assert.Equal("/verify-email", PublicAuthEndpoints.VerifyEmailRoute);
        Assert.Equal("/resend-verification", PublicAuthEndpoints.ResendVerificationRoute);
    }

    [Fact]
    public void Request_Shapes_Are_CamelCase()
    {
        var verifyNames = JsonNames(typeof(VerifyEmailRequest));
        Assert.Contains("token", verifyNames);

        var resendNames = JsonNames(typeof(ResendVerificationRequest));
        Assert.Contains("email", resendNames);
    }

    [Fact]
    public async Task Verify_With_Valid_Token_Activates_User()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await h.AuthService.RegisterParentAsync(new RegisterParentCommand(
            "verify@example.com", "HorseBatteryStaple!77", "Parent", null, "ar", true,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));

        var token = ExtractVerificationToken(h.Notifications.Dispatched[^1].Link);
        var result = await h.Verification.ConsumeAsync(token, Guid.NewGuid().ToString("D"));

        Assert.True(result.Success);
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.NormalizedEmail == "verify@example.com");
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(user.EmailVerified);
        Assert.Contains(h.Audit.Events, e => e.Action == "email_verified" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task Verify_With_Invalid_Token_Returns_TokenInvalid()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var result = await h.Verification.ConsumeAsync("not-a-real-token", Guid.NewGuid().ToString("D"));
        Assert.False(result.Success);
        Assert.Equal("token_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Verify_Cannot_Consume_Same_Token_Twice()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await h.AuthService.RegisterParentAsync(new RegisterParentCommand(
            "once@example.com", "HorseBatteryStaple!77", "Parent", null, "ar", true,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        var token = ExtractVerificationToken(h.Notifications.Dispatched[^1].Link);

        var first = await h.Verification.ConsumeAsync(token, Guid.NewGuid().ToString("D"));
        Assert.True(first.Success);

        var second = await h.Verification.ConsumeAsync(token, Guid.NewGuid().ToString("D"));
        Assert.False(second.Success);
        Assert.Equal("token_invalid", second.ErrorCode);
    }

    [Fact]
    public async Task Resend_Invalidates_Outstanding_Tokens_And_Issues_New_One()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await h.AuthService.RegisterParentAsync(new RegisterParentCommand(
            "resend@example.com", "HorseBatteryStaple!77", "Parent", null, "ar", true,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        var firstToken = ExtractVerificationToken(h.Notifications.Dispatched[^1].Link);

        var resend = await h.Verification.ResendAsync("resend@example.com", Guid.NewGuid().ToString("D"));
        Assert.True(resend.Success);
        Assert.False(string.IsNullOrWhiteSpace(resend.PlaintextToken));

        // First (original) token is now unusable.
        var stale = await h.Verification.ConsumeAsync(firstToken, Guid.NewGuid().ToString("D"));
        Assert.False(stale.Success);

        // Fresh token consumes OK.
        var fresh = await h.Verification.ConsumeAsync(resend.PlaintextToken!, Guid.NewGuid().ToString("D"));
        Assert.True(fresh.Success);
    }

    [Fact]
    public async Task Resend_For_Unknown_Email_Returns_Success_Without_Issuing_Token()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var result = await h.Verification.ResendAsync("nobody@example.com", Guid.NewGuid().ToString("D"));
        Assert.True(result.Success);
        Assert.Null(result.PlaintextToken);
        // No audit event emitted for silent-success enumeration defence.
        Assert.DoesNotContain(h.Audit.Events, e => e.Action == "verification_resent");
    }

    internal static string ExtractVerificationToken(string link)
    {
        var idx = link.IndexOf("token=", StringComparison.Ordinal);
        return Uri.UnescapeDataString(link[(idx + "token=".Length)..]);
    }

    private static string[] JsonNames(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
}
