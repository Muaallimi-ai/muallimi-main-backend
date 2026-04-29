using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Xunit;
#pragma warning disable CS8602

namespace Muallimi.MainBackend.Tests.Integration.Identity;

/// <summary>
/// T125 — End-to-end forgot-password → reset → login with new password.
/// Old password must be rejected after reset.
/// </summary>
public class PasswordResetCycleTests
{
    [Fact]
    public async Task ForgotPassword_Reset_OldPasswordRejected_NewPasswordAccepted()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        const string email = "reset@example.com";
        const string origPw = "OrigPass-1!";
        const string newPw  = "NewPass-99!";
        var corr = Guid.NewGuid().ToString("D");

        await h.SeedVerifiedParentAsync(email, origPw);

        // Login works before reset
        var loginBefore = await h.AuthService.LoginAsync(new(
            email, origPw, false, null, null, "127.0.0.1", null, corr));
        Assert.True(loginBefore.Success);

        // Initiate forgot-password — should dispatch a password_reset notification
        var forgotCmd = new ForgotPasswordCommand(email, "127.0.0.1", corr);
        await h.PasswordResetService.ForgotPasswordAsync(forgotCmd);
        var resetNotif = h.Notifications.Dispatched.FirstOrDefault(n => n.Kind == "password_reset");
        Assert.NotNull(resetNotif);

        // Consume the reset token and set a new password
        var resetToken = ExtractToken(resetNotif.Link);
        var resetCmd = new ResetPasswordCommand(resetToken, newPw, "127.0.0.1", null, corr);
        var resetOutcome = await h.PasswordResetService.ResetPasswordAsync(resetCmd);
        Assert.True(resetOutcome.Success);

        // Old password must be rejected
        var oldLogin = await h.AuthService.LoginAsync(new(
            email, origPw, false, null, null, "127.0.0.1", null, corr));
        Assert.False(oldLogin.Success);

        // New password accepted
        var newLogin = await h.AuthService.LoginAsync(new(
            email, newPw, false, null, null, "127.0.0.1", null, corr));
        Assert.True(newLogin.Success);
    }

    [Fact]
    public async Task ResetToken_Is_Single_Use()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        const string email = "singleuse@example.com";
        var corr = Guid.NewGuid().ToString("D");

        await h.SeedVerifiedParentAsync(email, "OldPass-1!");

        await h.PasswordResetService.ForgotPasswordAsync(new ForgotPasswordCommand(email, "127.0.0.1", corr));
        var resetToken = ExtractToken(h.Notifications.Dispatched
            .First(n => n.Kind == "password_reset").Link);

        var first = await h.PasswordResetService.ResetPasswordAsync(
            new ResetPasswordCommand(resetToken, "NewPass1-!", "127.0.0.1", null, corr));
        Assert.True(first.Success);

        // Second use of same token must fail
        var second = await h.PasswordResetService.ResetPasswordAsync(
            new ResetPasswordCommand(resetToken, "AnotherPass1!", "127.0.0.1", null, corr));
        Assert.False(second.Success);
    }

    [Fact]
    public async Task ResetPassword_RevokesAllActiveSessions()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        const string email = "sessionrevoke@example.com";
        var corr = Guid.NewGuid().ToString("D");

        await h.SeedVerifiedParentAsync(email, "OldPass-1!");

        // Login to create an active session
        var login = await h.AuthService.LoginAsync(new(
            email, "OldPass-1!", false, null, null, "127.0.0.1", null, corr));
        Assert.True(login.Success);

        // Get the session ID from the DB
        var userId2 = Guid.Parse(login.Payload!.UserId);
        var sessionRow = await h.Db.IdentityUserSessions
            .Where(s => s.UserId == userId2 && s.RevokedAt == null)
            .FirstOrDefaultAsync();
        Assert.NotNull(sessionRow);
        var sessionId = sessionRow!.Id;

        // Trigger forgot → reset
        await h.PasswordResetService.ForgotPasswordAsync(new ForgotPasswordCommand(email, "127.0.0.1", corr));
        var resetToken = ExtractToken(h.Notifications.Dispatched
            .Last(n => n.Kind == "password_reset").Link);
        await h.PasswordResetService.ResetPasswordAsync(
            new ResetPasswordCommand(resetToken, "NewPass1-!", "127.0.0.1", null, corr));

        // The old session must be revoked
        var stillActive = await h.Sessions.IsSessionActiveAsync(sessionId);
        Assert.False(stillActive);
    }

    private static string ExtractToken(string link)
    {
        if (link.Contains("token="))
        {
            var raw = link.Split("token=")[1];
            return Uri.UnescapeDataString(raw.Split('&')[0]);
        }
        return link;
    }
}
