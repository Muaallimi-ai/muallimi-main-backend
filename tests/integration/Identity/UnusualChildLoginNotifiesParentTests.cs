using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Services;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Identity;

/// <summary>
/// T143 — Integration test: unusual child login notifies the managing parent.
///
/// Verifies:
///   • First login from a new IP/UA that differs from any prior 30-day
///     successful login triggers <c>identity.child_unusual_login</c>
///     notification addressed to the parent.
///   • A repeat login from the same IP prefix does NOT trigger a second
///     notification.
///   • A Personal-user unusual login triggers <c>identity.unusual_login</c>
///     to themselves (not to a parent).
/// </summary>
public class UnusualChildLoginNotifiesParentTests
{
    [Fact]
    public async Task Second_Login_From_New_Ip_Sends_ChildUnusualLogin_To_Parent()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, tenantId) = await RegisterAndVerifyParentAsync(h, "p-unusual@example.com");
        var mgmt = BuildMgmt(h);

        var child = await mgmt.CreateChildAsync(new CreateChildCommand(
            parentId, tenantId, "تميم", null, 7, "male",
            new DateTime(2014, 6, 1), null, null, "ar",
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        Assert.True(child.Success);
        var username = child.Payload!.Username;
        var password = child.Payload.GeneratedPassword;

        var detector = new UnusualLoginDetector(h.Db);
        var auth = BuildAuthWithDetector(h, detector);

        // First login from 192.168.1.50 — no history yet, no flag.
        var login1 = await auth.LoginAsync(new LoginCommand(
            username, password, false, null,
            null,
            "192.168.1.50", "Mozilla/5.0 Desktop",
            Guid.NewGuid().ToString("D")));
        Assert.True(login1.Success);
        Assert.DoesNotContain(h.Notifications.Dispatched,
            n => n.Kind == "child_unusual_login");

        // Second login from a completely different /24 and UA.
        var login2 = await auth.LoginAsync(new LoginCommand(
            username, password, false, null,
            null,
            "10.50.30.20", "iPhone Safari/17",
            Guid.NewGuid().ToString("D")));
        Assert.True(login2.Success);
        Assert.Contains(h.Notifications.Dispatched,
            n => n.Kind == "child_unusual_login");
    }

    [Fact]
    public async Task Second_Login_From_Same_Subnet_Does_Not_Trigger_Notification()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, tenantId) = await RegisterAndVerifyParentAsync(h, "p-same-subnet@example.com");
        var mgmt = BuildMgmt(h);

        var child = await mgmt.CreateChildAsync(new CreateChildCommand(
            parentId, tenantId, "غازي", null, 5, "male",
            new DateTime(2016, 9, 9), null, null, "ar",
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        var username = child.Payload!.Username;
        var password = child.Payload.GeneratedPassword;

        var detector = new UnusualLoginDetector(h.Db);
        var auth = BuildAuthWithDetector(h, detector);

        // Two logins from the same /24 and same UA.
        var login1 = await auth.LoginAsync(new LoginCommand(
            username, password, false, null,
            null,
            "192.168.5.10", "Chrome/120",
            Guid.NewGuid().ToString("D")));
        Assert.True(login1.Success);

        h.Notifications.Dispatched.Clear();

        var login2 = await auth.LoginAsync(new LoginCommand(
            username, password, false, null,
            null,
            "192.168.5.25", "Chrome/120",
            Guid.NewGuid().ToString("D")));
        Assert.True(login2.Success);
        Assert.DoesNotContain(h.Notifications.Dispatched,
            n => n.Kind == "child_unusual_login");
    }

    [Fact]
    public async Task Personal_User_Unusual_Login_Notifies_Themselves()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await RegisterAndVerifyParentAsync(h, "personal-unusual@example.com");

        var detector = new UnusualLoginDetector(h.Db);
        var auth = BuildAuthWithDetector(h, detector);

        // First login — no history.
        var login1 = await auth.LoginAsync(new LoginCommand(
            "personal-unusual@example.com", "HorseBatteryStaple!77",
            false, null, null, "10.0.0.1", "Chrome", Guid.NewGuid().ToString("D")));
        Assert.True(login1.Success);

        // Second login from new IP.
        var login2 = await auth.LoginAsync(new LoginCommand(
            "personal-unusual@example.com", "HorseBatteryStaple!77",
            false, null, null, "172.20.50.1", "Firefox",
            Guid.NewGuid().ToString("D")));
        Assert.True(login2.Success);
        // Should get unusual_login (not child_unusual_login).
        Assert.Contains(h.Notifications.Dispatched, n => n.Kind == "unusual_login");
        Assert.DoesNotContain(h.Notifications.Dispatched, n => n.Kind == "child_unusual_login");
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

    private static UserManagementService BuildMgmt(IdentityTestHarness h)
        => new(h.Db, h.Passwords,
            new UsernameGenerator(new Random(77)),
            new ChildPasswordGenerator(new Random(66)),
            h.Audit.Emitter, h.Notifications,
            NullLogger<UserManagementService>.Instance);

    private static AuthService BuildAuthWithDetector(IdentityTestHarness h, UnusualLoginDetector detector)
        => new(
            h.Db, h.Passwords, h.Tokens,
            new NullRateLimitService(),
            h.Sessions, h.Audit.Emitter, h.Notifications,
            h.Verification,
            new VerificationLinkBuilder("http://test.local"),
            NullLogger<AuthService>.Instance,
            twoFactor: null,
            unusualLoginDetector: detector);
}
