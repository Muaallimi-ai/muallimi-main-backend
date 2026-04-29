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
/// T122 — Contract test for password self-service endpoints:
///   • POST /api/auth/change-password
///   • POST /api/auth/forgot-password
///   • POST /api/auth/reset-password
/// </summary>
public class PasswordSelfServiceContractTests
{
    [Fact]
    public void ChangePassword_Route_Is_Pinned()
    {
        Assert.Equal("/change-password", AuthenticatedEndpoints.ChangePasswordRoute);
    }

    [Fact]
    public void ForgotPassword_Route_Is_Pinned()
    {
        Assert.Equal("/forgot-password", AuthenticatedEndpoints.ForgotPasswordRoute);
    }

    [Fact]
    public void ResetPassword_Route_Is_Pinned()
    {
        Assert.Equal("/reset-password", AuthenticatedEndpoints.ResetPasswordRoute);
    }

    [Fact]
    public void ForgotPasswordCommand_Shape_Has_Required_Fields()
    {
        var props = typeof(ForgotPasswordCommand).GetProperties();
        var names = props.Select(p => p.Name).ToArray();
        Assert.Contains("Email", names);
        Assert.Contains("IpAddress", names);
        Assert.Contains("CorrelationId", names);
    }

    [Fact]
    public void ResetPasswordCommand_Shape_Has_Required_Fields()
    {
        var props = typeof(ResetPasswordCommand).GetProperties();
        var names = props.Select(p => p.Name).ToArray();
        Assert.Contains("Token", names);
        Assert.Contains("NewPassword", names);
        Assert.Contains("IpAddress", names);
        Assert.Contains("CorrelationId", names);
    }

    [Fact]
    public async Task ChangePassword_WithValidCurrentPassword_Succeeds()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (userId, _) = await h.SeedVerifiedParentAsync("chpw@example.com", "Temp-Pass-1!");

        var loginOutcome = await h.AuthService.LoginAsync(new(
            "chpw@example.com", "Temp-Pass-1!", false, null, null,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.True(loginOutcome.Success);

        // Retrieve the active session from DB to pass as the "current" session to preserve
        var activeSession = await h.Db.IdentityUserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .FirstOrDefaultAsync();
        var currentSessionId = activeSession?.Id ?? Guid.Empty;

        var chOutcome = await h.PasswordResetService.ChangePasswordAsync(new ChangePasswordCommand(
            UserId: userId,
            CurrentPassword: "Temp-Pass-1!",
            NewPassword: "NewSecure-2!",
            IpAddress: "127.0.0.1",
            UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")),
            currentSessionId: currentSessionId);
        Assert.True(chOutcome.Success);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns_401()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (userId, _) = await h.SeedVerifiedParentAsync("chpw2@example.com", "Temp-Pass-1!");
        var outcome = await h.PasswordResetService.ChangePasswordAsync(new ChangePasswordCommand(
            UserId: userId,
            CurrentPassword: "WrongPassword-1!",
            NewPassword: "NewSecure-2!",
            IpAddress: "127.0.0.1",
            UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")),
            currentSessionId: Guid.Empty);
        Assert.False(outcome.Success);
        Assert.Equal(401, outcome.HttpStatus);
    }

    // Helpers
    private static string[] JsonNames(Type type) =>
        type.GetProperties()
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
}
