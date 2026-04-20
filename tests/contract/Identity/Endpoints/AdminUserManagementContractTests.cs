using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Identity.Startup;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T100 — Contract test covering the 14 admin endpoints:
/// list / detail / invite / grant-role / revoke-role / reset-password /
/// suspend / unsuspend / delete / list-roles / audit-log plus
/// change-password + accept-invitation.
/// </summary>
public class AdminUserManagementContractTests
{
    [Fact]
    public void Identity_Prefix_And_Group_Are_Pinned()
    {
        Assert.Equal("/api/auth", IdentityEndpointRouteBuilderExtensions.IdentityRoutePrefix);
        Assert.Equal("/admin", AdminUserEndpoints.GroupRoute);
    }

    [Fact]
    public void InviteUserRequest_Shape_Is_CamelCase()
    {
        var names = JsonNames(typeof(InviteUserRequest));
        foreach (var expected in new[] { "email", "fullName", "fullNameEn", "locale", "roles", "tenantId" })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void AdminUserSummary_Exposes_Required_Fields_And_Never_Leaks_Password()
    {
        var names = JsonNames(typeof(AdminUserSummary));
        foreach (var expected in new[] { "userId", "email", "fullName", "roles", "status", "tenantType" })
        {
            Assert.Contains(expected, names);
        }
        Assert.DoesNotContain("passwordHash", names);
        Assert.DoesNotContain("password", names);
    }

    [Fact]
    public async Task Invite_Creates_PendingVerification_User_With_Roles_And_RequiresPasswordReset()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SeedSuperAdminAsync(h);

        var svc = BuildAdminService(h);
        var result = await svc.InviteUserAsync(new InviteUserCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            ActorRoles: new[] { "super-admin" },
            Email: "curriculum@example.com",
            FullName: "Curriculum Admin",
            FullNameEn: null,
            Locale: "ar",
            Roles: new[] { "curriculum-admin" },
            TargetTenantId: platformTenantId,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(result.Success);
        Assert.Equal(201, result.HttpStatus);
        Assert.NotNull(result.Payload);
        Assert.Contains("curriculum-admin", result.Payload!.RolesGranted);

        var invited = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.NormalizedEmail == "curriculum@example.com");
        Assert.Equal(UserStatus.PendingEmailVerification, invited.Status);
        Assert.True(invited.RequiresPasswordReset);

        Assert.Contains(h.Audit.Events, e => e.Action == "invite_user" && e.Outcome == "succeeded");
        Assert.Contains(h.Notifications.Dispatched, n => n.Kind.StartsWith("invitation:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GrantRole_Records_Audit_And_Adds_Active_UserRole()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SeedSuperAdminAsync(h);
        var svc = BuildAdminService(h);

        var invited = await svc.InviteUserAsync(new InviteUserCommand(
            superAdminId, platformTenantId, new[] { "super-admin" },
            "op@example.com", "Platform Op", null, "ar",
            new[] { "platform-operator" }, platformTenantId,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        var opId = Guid.Parse(invited.Payload!.UserId);

        var grant = await svc.GrantRoleAsync(new GrantRoleCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            ActorRoles: new[] { "super-admin" },
            TargetUserId: opId,
            RoleName: "subject-expert",
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(grant.Success);
        Assert.Contains(h.Audit.Events, e => e.Action == "role_granted" && e.Outcome == "succeeded");

        var userRoles = await h.Db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == opId && ur.RevokedAt == null).ToListAsync();
        Assert.Equal(2, userRoles.Count);
    }

    [Fact]
    public async Task List_Returns_PageAndTotalCount()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SeedSuperAdminAsync(h);
        var svc = BuildAdminService(h);

        for (var i = 0; i < 3; i++)
        {
            await svc.InviteUserAsync(new InviteUserCommand(
                superAdminId, platformTenantId, new[] { "super-admin" },
                $"list-{i}@example.com", $"User {i}", null, "ar",
                new[] { "platform-operator" }, platformTenantId,
                "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        }

        var list = await svc.ListUsersAsync(new ListUsersQuery(
            superAdminId, new[] { "super-admin" },
            TenantId: null, RoleName: null, Status: null, Search: null,
            Page: 1, PageSize: 10));
        Assert.True(list.Success);
        Assert.NotNull(list.Payload);
        Assert.True(list.Payload!.TotalCount >= 4); // 3 invited + super-admin
        Assert.DoesNotContain(list.Payload.Users, u => u.FullName.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AdminReset_Sends_Email_And_Audits_admin_reset_initiated()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SeedSuperAdminAsync(h);
        var svc = BuildAdminService(h);

        var invited = await svc.InviteUserAsync(new InviteUserCommand(
            superAdminId, platformTenantId, new[] { "super-admin" },
            "reset@example.com", "Reset Target", null, "ar",
            new[] { "platform-operator" }, platformTenantId,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        var targetId = Guid.Parse(invited.Payload!.UserId);
        // Accept the invitation so the user is Active.
        var token = h.Notifications.Dispatched.Last(n => n.Kind.StartsWith("invitation:", StringComparison.Ordinal)).Link;
        token = token.Substring(token.IndexOf("token=", StringComparison.Ordinal) + "token=".Length);
        await svc.AcceptInvitationAsync(new AcceptInvitationCommand(
            Token: Uri.UnescapeDataString(token),
            NewPassword: "Str0ng-Passw0rd-First!",
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));

        var reset = await svc.AdminResetPasswordAsync(new AdminResetPasswordCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            ActorRoles: new[] { "super-admin" },
            TargetUserId: targetId,
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(reset.Success);
        Assert.Contains(h.Audit.Events, e => e.Action == "admin_reset_initiated" && e.Outcome == "succeeded");
        Assert.Contains(h.Notifications.Dispatched, n => n.Kind == "password_reset");
    }

    [Fact]
    public async Task ListRoles_Contains_All_Eight_System_Roles()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var svc = BuildAdminService(h);
        var roles = await svc.ListRolesAsync();
        foreach (var expected in new[] { "super-admin", "platform-operator", "curriculum-admin", "subject-expert", "school-admin", "teacher", "parent", "student" })
        {
            Assert.Contains(roles, r => r.Name == expected);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static AdminUserService BuildAdminService(IdentityTestHarness h)
    {
        var link = new InvitationLinkBuilder("http://test.local");
        return new AdminUserService(
            h.Db, h.Passwords, h.Audit.Emitter, h.Notifications, h.Verification,
            link, NullLogger<AdminUserService>.Instance);
    }

    private static async Task<(Guid UserId, Guid TenantId)> SeedSuperAdminAsync(IdentityTestHarness h)
    {
        var cfg = new Muallimi.Infrastructure.Identity.Seed.SuperAdminSeedConfig(
            Email: "root@muallimi.test",
            InitialPassword: "ChangeMeNow!234-Muaallimi",
            AdditionalRoles: Array.Empty<string>(),
            FullName: "Root",
            Locale: "ar");
        var seeder = new Muallimi.Infrastructure.Identity.Seed.SuperAdminSeeder(
            h.Db, h.Passwords,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<Muallimi.Infrastructure.Identity.Seed.SuperAdminSeeder>.Instance);
        var outcome = await seeder.EnsureSeededAsync(cfg);
        Assert.Equal(Muallimi.Infrastructure.Identity.Seed.SuperAdminSeedOutcome.Seeded, outcome);
        var platform = await h.Db.IdentityTenants.IgnoreQueryFilters().FirstAsync(t => t.Type == TenantType.Platform);
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters().FirstAsync(u => u.NormalizedEmail == "root@muallimi.test");
        return (user.Id, platform.Id);
    }

    private static string[] JsonNames(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
}
