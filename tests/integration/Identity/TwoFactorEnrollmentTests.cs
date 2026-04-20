using System;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using OtpNet;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Identity;

/// <summary>
/// T126 — End-to-end 2FA: enable → verify → login-with-TOTP → disable.
/// </summary>
public class TwoFactorEnrollmentTests
{
    [Fact]
    public async Task Enable_Verify_Login_Disable_Cycle()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        const string email = "2fa@example.com";
        const string pw = "SecurePass-2!";
        var corr = Guid.NewGuid().ToString("D");

        // Register + verify
        var reg = await h.AuthService.RegisterParentAsync(new(
            email, pw, "مستخدم", null, "ar", true, "127.0.0.1", null, corr));
        Assert.True(reg.Success);
        var evToken = ExtractToken(h.Notifications.Dispatched
            .First(n => n.Kind == "email_verification").Link);
        await h.Verification.ConsumeAsync(evToken, corr);
        var userId = Guid.Parse(reg.Payload!.UserId);

        // Step 1: enable — returns QR uri + temp secret
        var enableOutcome = await h.TwoFactorManagement.StartEnrollmentAsync(
            new EnableTwoFactorCommand(userId, corr));
        Assert.True(enableOutcome.Success);
        Assert.NotEmpty(enableOutcome.QrUri!);
        Assert.NotEmpty(enableOutcome.TempSecret!);

        // Step 2: generate a valid TOTP code from the temp secret and verify
        var code = GenerateTotpCode(enableOutcome.TempSecret!);
        var verifyOutcome = await h.TwoFactorManagement.VerifyEnrollmentAsync(
            new VerifyTwoFactorCommand(userId, code, corr));
        Assert.True(verifyOutcome.Success);
        Assert.True(verifyOutcome.RecoveryCodes!.Count > 0);

        // Step 3: login requires TOTP now
        var loginNoTotp = await h.AuthService.LoginAsync(new(
            email, pw, false, null, null, "127.0.0.1", null, corr));
        Assert.False(loginNoTotp.Success);
        Assert.Equal("two_factor_required", loginNoTotp.ErrorCode);

        var codeForLogin = GenerateTotpCode(enableOutcome.TempSecret!);
        var loginWithTotp = await h.AuthService.LoginAsync(new(
            email, pw, false, codeForLogin, null, "127.0.0.1", null, corr));
        Assert.True(loginWithTotp.Success);

        // Step 4: disable — requires current password
        var disableOutcome = await h.TwoFactorManagement.DisableAsync(
            new DisableTwoFactorCommand(userId, pw, corr));
        Assert.True(disableOutcome.Success);

        // Login without TOTP works again
        var loginAfterDisable = await h.AuthService.LoginAsync(new(
            email, pw, false, null, null, "127.0.0.1", null, corr));
        Assert.True(loginAfterDisable.Success);
    }

    [Fact]
    public async Task Wrong_Totp_Code_Rejects_Enrollment()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        const string email = "2fabad@example.com";
        var corr = Guid.NewGuid().ToString("D");

        var reg = await h.AuthService.RegisterParentAsync(new(
            email, "SecurePass-2!", "مستخدم", null, "ar", true, "127.0.0.1", null, corr));
        var evToken = ExtractToken(h.Notifications.Dispatched
            .First(n => n.Kind == "email_verification").Link);
        await h.Verification.ConsumeAsync(evToken, corr);
        var userId = Guid.Parse(reg.Payload!.UserId);

        var enableOutcome = await h.TwoFactorManagement.StartEnrollmentAsync(
            new EnableTwoFactorCommand(userId, corr));
        Assert.True(enableOutcome.Success);

        var verifyOutcome = await h.TwoFactorManagement.VerifyEnrollmentAsync(
            new VerifyTwoFactorCommand(userId, "000000", corr));
        Assert.False(verifyOutcome.Success);
    }

    private static string GenerateTotpCode(string base32Secret)
    {
        var padded = base32Secret.Length % 8 == 0
            ? base32Secret
            : base32Secret + new string('=', 8 - base32Secret.Length % 8);
        var secret = Base32Encoding.ToBytes(padded);
        var totp = new Totp(secret, step: 30, mode: OtpHashMode.Sha1, totpSize: 6);
        return totp.ComputeTotp();
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
