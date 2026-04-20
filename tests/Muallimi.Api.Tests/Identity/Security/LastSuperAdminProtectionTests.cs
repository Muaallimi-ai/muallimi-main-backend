using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Identity.Seed;
using Xunit;

namespace Muallimi.Api.Tests.Identity.Security;

/// <summary>
/// T102 — Security test: the last active super-admin cannot be deleted,
/// suspended, or have their <c>super-admin</c> role revoked. Each
/// invariant maps to the <c>last_super_admin</c> error code from the
/// identity HTTP contract.
/// </summary>
public class LastSuperAdminProtectionTests
{
    [Fact]
    public async Task Cannot_Delete_Last_SuperAdmin()
    {
        var (h, svc, id, tenantId) = await SeedAsync();
        var result = await svc.DeleteUserAsync(new DeleteUserCommand(
            id, tenantId, new[] { "super-admin" }, id,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.False(result.Success);
        Assert.Equal(409, result.HttpStatus);
        Assert.Equal("last_super_admin", result.ErrorCode);
    }

    [Fact]
    public async Task Cannot_Suspend_Last_SuperAdmin()
    {
        var (h, svc, id, tenantId) = await SeedAsync();
        var result = await svc.SuspendUserAsync(new SuspendUserCommand(
            id, tenantId, new[] { "super-admin" }, id,
            Reason: "test", IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.False(result.Success);
        Assert.Equal("last_super_admin", result.ErrorCode);
    }

    [Fact]
    public async Task Cannot_Revoke_SuperAdmin_Role_From_Last_SuperAdmin()
    {
        var (h, svc, id, tenantId) = await SeedAsync();
        var result = await svc.RevokeRoleAsync(new RevokeRoleCommand(
            id, tenantId, new[] { "super-admin" }, id,
            "super-admin",
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.False(result.Success);
        Assert.Equal("last_super_admin", result.ErrorCode);
    }

    [Fact]
    public async Task Can_Revoke_After_A_Second_SuperAdmin_Exists()
    {
        var (h, svc, firstId, tenantId) = await SeedAsync();

        // Invite + accept a second super-admin so the first can be revoked.
        var invite = await svc.InviteUserAsync(new InviteUserCommand(
            firstId, tenantId, new[] { "super-admin" },
            "second@muallimi.test", "Second Admin", null, "ar",
            new[] { "super-admin" }, tenantId,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.True(invite.Success);
        var secondId = Guid.Parse(invite.Payload!.UserId);
        var token = h.Notifications.Dispatched.Last(n => n.Kind.StartsWith("invitation:", StringComparison.Ordinal)).Link;
        token = Uri.UnescapeDataString(token.Substring(token.IndexOf("token=", StringComparison.Ordinal) + "token=".Length));
        await svc.AcceptInvitationAsync(new AcceptInvitationCommand(
            token, "Second-Admin-Str0ng!", "127.0.0.1", null, Guid.NewGuid().ToString("D")));

        var revoke = await svc.RevokeRoleAsync(new RevokeRoleCommand(
            firstId, tenantId, new[] { "super-admin" }, firstId,
            "super-admin",
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.True(revoke.Success);
    }

    private static async Task<(IdentityTestHarness Harness, AdminUserService Service, Guid SuperAdminId, Guid PlatformTenantId)> SeedAsync()
    {
        var h = await IdentityTestHarness.CreateAsync();
        var seeder = new SuperAdminSeeder(h.Db, h.Passwords,
            new ConfigurationBuilder().Build(),
            NullLogger<SuperAdminSeeder>.Instance);
        await seeder.EnsureSeededAsync(new SuperAdminSeedConfig(
            "root@muallimi.test", "ChangeMeNow!234-Muaallimi", Array.Empty<string>()));
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters().FirstAsync(u => u.NormalizedEmail == "root@muallimi.test");
        var tenant = await h.Db.IdentityTenants.IgnoreQueryFilters().FirstAsync(t => t.Type == TenantType.Platform);

        var svc = new AdminUserService(
            h.Db, h.Passwords, h.Audit.Emitter, h.Notifications, h.Verification,
            new InvitationLinkBuilder("http://test.local"),
            NullLogger<AdminUserService>.Instance);
        return (h, svc, user.Id, tenant.Id);
    }
}
