using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Tests.Identity;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Identity.Seed;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Identity;

/// <summary>
/// T101 — Integration test for the super-admin bootstrap. Pins:
///   • seeding with SUPER_ADMIN_EMAIL + SUPER_ADMIN_INITIAL_PASSWORD
///     creates exactly one super-admin with RequiresPasswordReset=true;
///   • running a second time is a no-op (idempotent);
///   • if a super-admin already exists, seeding with a different email
///     is a no-op.
/// </summary>
public class SuperAdminBootstrapTests
{
    [Fact]
    public async Task Seeder_Creates_SuperAdmin_With_RequiresPasswordReset()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var seeder = new SuperAdminSeeder(h.Db, h.Passwords,
            new ConfigurationBuilder().Build(),
            NullLogger<SuperAdminSeeder>.Instance);
        var cfg = new SuperAdminSeedConfig(
            Email: "root@muallimi.test",
            InitialPassword: "ChangeMeNow!234-Muaallimi",
            AdditionalRoles: Array.Empty<string>());
        var outcome = await seeder.EnsureSeededAsync(cfg);

        Assert.Equal(SuperAdminSeedOutcome.Seeded, outcome);
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.NormalizedEmail == "root@muallimi.test");
        Assert.True(user.RequiresPasswordReset);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(user.EmailVerified);
        Assert.False(user.TwoFactorEnabled); // Flagged for enrollment on first login (US4).

        var tenant = await h.Db.IdentityTenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == user.TenantId);
        Assert.Equal(TenantType.Platform, tenant.Type);

        var hasSuperAdminRole = await h.Db.IdentityUserRoles.IgnoreQueryFilters()
            .AnyAsync(ur => ur.UserId == user.Id && ur.RevokedAt == null
                && h.Db.IdentityRoles.IgnoreQueryFilters().Any(r => r.Id == ur.RoleId && r.Name == "super-admin"));
        Assert.True(hasSuperAdminRole);
    }

    [Fact]
    public async Task Second_Boot_Is_No_Op()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var seeder = new SuperAdminSeeder(h.Db, h.Passwords,
            new ConfigurationBuilder().Build(),
            NullLogger<SuperAdminSeeder>.Instance);
        var cfg = new SuperAdminSeedConfig("root@muallimi.test", "ChangeMeNow!234-Muaallimi", Array.Empty<string>());

        var first = await seeder.EnsureSeededAsync(cfg);
        var second = await seeder.EnsureSeededAsync(cfg);

        Assert.Equal(SuperAdminSeedOutcome.Seeded, first);
        Assert.Equal(SuperAdminSeedOutcome.AlreadySeeded, second);

        var count = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .CountAsync(u => u.NormalizedEmail == "root@muallimi.test");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Existing_SuperAdmin_Blocks_New_Seed_With_Different_Email()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var seeder = new SuperAdminSeeder(h.Db, h.Passwords,
            new ConfigurationBuilder().Build(),
            NullLogger<SuperAdminSeeder>.Instance);

        await seeder.EnsureSeededAsync(new SuperAdminSeedConfig("a@muallimi.test", "ChangeMeNow!234-Muaallimi", Array.Empty<string>()));
        var outcome = await seeder.EnsureSeededAsync(new SuperAdminSeedConfig("b@muallimi.test", "ChangeMeNow!234-Muaallimi", Array.Empty<string>()));

        Assert.Equal(SuperAdminSeedOutcome.AlreadySeeded, outcome);
        var bCount = await h.Db.IdentityUsers.IgnoreQueryFilters().CountAsync(u => u.NormalizedEmail == "b@muallimi.test");
        Assert.Equal(0, bCount);
    }

    [Fact]
    public async Task Empty_Env_Vars_Skip_Seeding()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var seeder = new SuperAdminSeeder(h.Db, h.Passwords,
            new ConfigurationBuilder().Build(),
            NullLogger<SuperAdminSeeder>.Instance);
        var outcome = await seeder.EnsureSeededAsync(new SuperAdminSeedConfig(null, null, Array.Empty<string>()));

        Assert.Equal(SuperAdminSeedOutcome.Skipped, outcome);
        var anyUser = await h.Db.IdentityUsers.IgnoreQueryFilters().AnyAsync();
        Assert.False(anyUser);
    }
}
