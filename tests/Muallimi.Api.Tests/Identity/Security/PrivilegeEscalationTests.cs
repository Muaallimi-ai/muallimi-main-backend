using System;
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
/// T103 — Non-platform role holders cannot grant Platform-scoped roles.
/// Only <c>super-admin</c> may grant Platform roles; other actors
/// attempting to do so receive <c>privilege_escalation</c> and no
/// UserRole row is written.
/// </summary>
public class PrivilegeEscalationTests
{
    [Fact]
    public async Task PlatformOperator_Cannot_Grant_Platform_Role()
    {
        var (h, svc, targetUserId, tenantId) = await SeedAsync();

        var result = await svc.GrantRoleAsync(new GrantRoleCommand(
            ActorUserId: Guid.NewGuid(),
            ActorTenantId: tenantId,
            ActorRoles: new[] { "platform-operator" }, // NOT super-admin
            TargetUserId: targetUserId,
            RoleName: "curriculum-admin",
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.False(result.Success);
        Assert.Equal(403, result.HttpStatus);
        Assert.Equal("privilege_escalation", result.ErrorCode);
    }

    [Fact]
    public async Task Parent_Cannot_Invite_Platform_User()
    {
        var (_, svc, _, tenantId) = await SeedAsync();
        var result = await svc.InviteUserAsync(new InviteUserCommand(
            ActorUserId: Guid.NewGuid(),
            ActorTenantId: tenantId,
            ActorRoles: new[] { "parent" },
            Email: "evil@example.com",
            FullName: "Evil",
            FullNameEn: null,
            Locale: "ar",
            Roles: new[] { "super-admin" },
            TargetTenantId: tenantId,
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.False(result.Success);
        Assert.Equal("privilege_escalation", result.ErrorCode);
    }

    [Fact]
    public async Task SuperAdmin_Can_Grant_Any_Platform_Role()
    {
        var (h, svc, targetUserId, tenantId) = await SeedAsync();
        var rootId = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .Where(u => u.NormalizedEmail == "root@muallimi.test")
            .Select(u => u.Id).FirstAsync();

        var result = await svc.GrantRoleAsync(new GrantRoleCommand(
            ActorUserId: rootId,
            ActorTenantId: tenantId,
            ActorRoles: new[] { "super-admin" },
            TargetUserId: targetUserId,
            RoleName: "subject-expert",
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(result.Success);
    }

    private static async Task<(IdentityTestHarness Harness, AdminUserService Service, Guid TargetUserId, Guid PlatformTenantId)> SeedAsync()
    {
        var h = await IdentityTestHarness.CreateAsync();
        var seeder = new SuperAdminSeeder(h.Db, h.Passwords,
            new ConfigurationBuilder().Build(),
            NullLogger<SuperAdminSeeder>.Instance);
        await seeder.EnsureSeededAsync(new SuperAdminSeedConfig(
            "root@muallimi.test", "ChangeMeNow!234-Muaallimi", Array.Empty<string>()));
        var platform = await h.Db.IdentityTenants.IgnoreQueryFilters().FirstAsync(t => t.Type == TenantType.Platform);
        var rootId = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .Where(u => u.NormalizedEmail == "root@muallimi.test")
            .Select(u => u.Id).FirstAsync();

        var svc = new AdminUserService(
            h.Db, h.Passwords, h.Audit.Emitter, h.Notifications, h.Verification,
            new InvitationLinkBuilder("http://test.local"),
            NullLogger<AdminUserService>.Instance);

        // Seed a target user we can target for role grants.
        var invite = await svc.InviteUserAsync(new InviteUserCommand(
            rootId, platform.Id, new[] { "super-admin" },
            "op-target@example.com", "Op Target", null, "ar",
            new[] { "platform-operator" }, platform.Id,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.True(invite.Success);
        return (h, svc, Guid.Parse(invite.Payload!.UserId), platform.Id);
    }
}
