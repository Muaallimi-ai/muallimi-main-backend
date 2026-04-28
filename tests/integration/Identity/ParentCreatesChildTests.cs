using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Identity;

/// <summary>
/// T084 — End-to-end walkthrough: parent registers, verifies email,
/// creates a child, the child logs in with the returned credentials,
/// credentials never appear again on subsequent reads, and an old
/// password stops working after regeneration.
/// </summary>
public class ParentCreatesChildTests
{
    [Fact]
    public async Task Parent_Creates_Child_Child_Logs_In_Credentials_Returned_Once()
    {
        using var h = await IdentityTestHarness.CreateAsync();

        var parentEmail = "parent-e2e@example.com";
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync(parentEmail);
        var parent = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == parentId);

        var svc = new UserManagementService(
            h.Db, h.Passwords,
            new UsernameGenerator(new Random(7)),
            new ChildPasswordGenerator(new Random(7)),
            h.Audit.Emitter, h.Notifications,
            NullLogger<UserManagementService>.Instance,
            new WeakPinBlocklist());

        var created = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parent.Id,
            parentTenantId: parent.TenantId,
            fullName: "الطفل",
            grade: 3,
            gender: "male",
            birthYear: 2018,
            birthMonth: 6));
        Assert.True(created.Success);
        var childUsername = created.Payload!.Username;
        var childPassword = created.Payload.GeneratedPassword;

        // The child can log in with the returned credentials.
        var login = await h.AuthService.LoginAsync(new LoginCommand(
            Identifier: childUsername,
            Password: childPassword,
            RememberMe: false,
            TwoFactorCode: null,
            TempToken: null,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(login.Success);
        Assert.Equal(200, login.HttpStatus);
        Assert.False(string.IsNullOrEmpty(login.Payload!.AccessToken));
        Assert.Contains("student", login.Payload.Roles);
        Assert.Equal("family", login.Payload.TenantType);

        // Subsequent reads never reveal the password.
        var list = await svc.ListChildrenAsync(parent.Id, parent.TenantId);
        Assert.Single(list);
        Assert.DoesNotContain(list[0].GetType().GetProperties(),
            p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));

        // After regeneration the old password must stop working.
        var childId = Guid.Parse(created.Payload.UserId);
        var regen = await svc.RegenerateChildPasswordAsync(new RegenerateChildPasswordCommand(
            parent.Id, parent.TenantId, childId,
            CustomPassword: null, PasswordLocale: "ar",
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(regen.Success);
        Assert.NotEqual(childPassword, regen.Payload!.GeneratedPassword);

        var oldPasswordAttempt = await h.AuthService.LoginAsync(new LoginCommand(
            Identifier: childUsername,
            Password: childPassword,
            RememberMe: false,
            TwoFactorCode: null,
            TempToken: null,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.False(oldPasswordAttempt.Success);
        Assert.Equal("invalid_credentials", oldPasswordAttempt.ErrorCode);

        var newPasswordAttempt = await h.AuthService.LoginAsync(new LoginCommand(
            Identifier: childUsername,
            Password: regen.Payload.GeneratedPassword,
            RememberMe: false,
            TwoFactorCode: null,
            TempToken: null,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(newPasswordAttempt.Success);
    }
}
