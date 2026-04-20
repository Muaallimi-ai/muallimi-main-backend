using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.Api.Tests.Identity.Security;

/// <summary>
/// T067 — Security test: 5 failed login attempts within the window
/// transition the user to <see cref="UserStatus.Locked"/> with a 15
/// minute <c>LockoutEnd</c>. The 6th attempt (even with the correct
/// password) is refused with <c>account_locked</c>.
/// </summary>
public class BruteForceLockoutTests
{
    [Fact]
    public async Task Five_Failed_Logins_Lock_The_Account_For_Fifteen_Minutes()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await Muallimi.MainBackend.Tests.Contract.Identity.Endpoints.LoginContractTests
            .RegisterAndVerifyAsync(h, "lock@example.com", "CorrectBatteryStaple!77");

        for (var i = 0; i < 5; i++)
        {
            var outcome = await h.AuthService.LoginAsync(new LoginCommand(
                "lock@example.com", "WrongPassword!" + i,
                RememberMe: false, TwoFactorCode: null, TempToken: null,
                IpAddress: "127.0.0.1", UserAgent: "xunit",
                CorrelationId: Guid.NewGuid().ToString("D")));
            Assert.False(outcome.Success);
        }

        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.NormalizedEmail == "lock@example.com");
        Assert.Equal(UserStatus.Locked, user.Status);
        Assert.NotNull(user.LockoutEnd);
        Assert.True(user.LockoutEnd!.Value > DateTime.UtcNow.AddMinutes(14));
        Assert.True(user.LockoutEnd.Value < DateTime.UtcNow.AddMinutes(16));
        Assert.Equal(5, user.FailedLoginAttempts);

        // Sixth attempt with the correct password is still refused while locked.
        var blocked = await h.AuthService.LoginAsync(new LoginCommand(
            "lock@example.com", "CorrectBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.False(blocked.Success);
        Assert.Equal(423, blocked.HttpStatus);
        Assert.Equal("account_locked", blocked.ErrorCode);

        // Audit trail shows exactly one lockout event.
        var lockoutEvents = h.Audit.Events.Count(e => e.Action == "login_locked");
        Assert.Equal(1, lockoutEvents);
    }

    [Fact]
    public async Task Lockout_Clears_After_Window_Elapses_And_Correct_Password()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var user = await Muallimi.MainBackend.Tests.Contract.Identity.Endpoints.LoginContractTests
            .RegisterAndVerifyAsync(h, "unlock@example.com", "CorrectBatteryStaple!77");

        user.Status = UserStatus.Locked;
        user.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
        user.FailedLoginAttempts = 5;
        await h.Db.SaveChangesAsync();

        var outcome = await h.AuthService.LoginAsync(new LoginCommand(
            "unlock@example.com", "CorrectBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(outcome.Success);

        await h.Db.Entry(user).ReloadAsync();
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(user.LockoutEnd);
        Assert.Equal(0, user.FailedLoginAttempts);
    }
}
