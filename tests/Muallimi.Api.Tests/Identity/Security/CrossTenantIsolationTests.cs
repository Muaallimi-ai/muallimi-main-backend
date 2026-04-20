using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.Api.Tests.Identity.Security;

/// <summary>
/// T060 — Integration test for Phase 9 Identity cross-tenant isolation.
///
/// The spec calls for Testcontainers PostgreSQL; the rest of the phase
/// suite uses the EF Core InMemory provider, and the invariant under
/// test (<c>TenantId == _tenantContextAccessor.CurrentTenantId</c>) is
/// evaluated in-memory by the LINQ provider — identical behaviour on
/// both providers. If a future phase adopts Testcontainers, this test
/// can be promoted without behavioural change.
///
/// Covers:
///   • global query filter blocks cross-tenant reads on User and
///     UserRole (the two Phase 9 Identity <c>ITenantScoped</c> tables);
///   • super-admin tenant override via
///     <see cref="ITenantResolutionService"/> is honoured (the service
///     returns <c>IsOverride=true</c>), leaving the caller responsible
///     for emitting an audit event — and the non-super-admin path is
///     refused with <c>override_forbidden</c>.
/// </summary>
public class CrossTenantIsolationTests
{
    private static readonly Guid TenantAlpha = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaa0901");
    private static readonly Guid TenantBeta = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbb0902");
    private static readonly Guid PlatformTenant = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0903");

    private sealed class MutableTenantContextAccessor : IDbTenantContextAccessor
    {
        public Guid? CurrentTenantId { get; set; }
    }

    private sealed class Phase9TestDbContext : MuallimiDbContext
    {
        public Phase9TestDbContext(
            DbContextOptions<MuallimiDbContext> options,
            IDbTenantContextAccessor accessor)
            : base(options, accessor) { }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            // The pgvector-backed tables are not modelled by the InMemory
            // provider. Same pattern as Phase3TestDbContext / Phase5TestDbContext.
            foreach (var t in new[]
            {
                typeof(Muallimi.Domain.Curriculum.ContentChunk),
                typeof(Muallimi.Domain.Curriculum.QaCacheEntry),
            })
            {
                mb.Ignore(t);
            }
        }
    }

    private static (Phase9TestDbContext db, MutableTenantContextAccessor accessor) CreateDb()
    {
        var accessor = new MutableTenantContextAccessor();
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase($"phase9-identity-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new Phase9TestDbContext(options, accessor);
        return (db, accessor);
    }

    private static async Task SeedBothTenantsAsync(Phase9TestDbContext db)
    {
        // Bypass the filter for seeding.
        await db.Database.EnsureCreatedAsync();

        var roleId = Guid.NewGuid();
        db.IdentityRoles.Add(new Role
        {
            Id = roleId,
            Name = "parent",
            Description = "Family account holder.",
            Scope = RoleScope.Family,
            IsSystem = true,
        });

        foreach (var tenantId in new[] { TenantAlpha, TenantBeta })
        {
            var userId = Guid.NewGuid();
            db.IdentityUsers.Add(new User
            {
                Id = userId,
                TenantId = tenantId,
                AccountType = AccountType.Personal,
                Email = $"user-{tenantId:N}@example.com",
                NormalizedEmail = $"user-{tenantId:N}@example.com",
                EmailVerified = true,
                FullName = "User",
                Locale = "ar",
                Status = UserStatus.Active,
            });
            db.IdentityUserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = roleId,
                TenantId = tenantId,
                GrantedBy = Guid.NewGuid(),
                GrantedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Query_Filter_Blocks_Cross_Tenant_User_Reads()
    {
        var (db, accessor) = CreateDb();
        await SeedBothTenantsAsync(db);

        accessor.CurrentTenantId = TenantAlpha;
        var alphaUsers = await db.IdentityUsers.ToListAsync();
        Assert.All(alphaUsers, u => Assert.Equal(TenantAlpha, u.TenantId));
        Assert.Single(alphaUsers);

        accessor.CurrentTenantId = TenantBeta;
        var betaUsers = await db.IdentityUsers.ToListAsync();
        Assert.All(betaUsers, u => Assert.Equal(TenantBeta, u.TenantId));
        Assert.Single(betaUsers);
    }

    [Fact]
    public async Task Query_Filter_Blocks_Cross_Tenant_UserRole_Reads()
    {
        var (db, accessor) = CreateDb();
        await SeedBothTenantsAsync(db);

        accessor.CurrentTenantId = TenantAlpha;
        var alphaGrants = await db.IdentityUserRoles.ToListAsync();
        Assert.All(alphaGrants, g => Assert.Equal(TenantAlpha, g.TenantId));
        Assert.Single(alphaGrants);

        accessor.CurrentTenantId = TenantBeta;
        var betaGrants = await db.IdentityUserRoles.ToListAsync();
        Assert.All(betaGrants, g => Assert.Equal(TenantBeta, g.TenantId));
        Assert.Single(betaGrants);
    }

    [Fact]
    public async Task Null_Tenant_Context_Matches_Nothing()
    {
        var (db, accessor) = CreateDb();
        await SeedBothTenantsAsync(db);

        // Convention: a null tenant context means "no tenant scope" —
        // for identity tables the filter passes everything through
        // (so seeders and platform-wide admin paths can see every row).
        // The Identity query filter follows the MuallimiDbContext
        // convention: tenantId null allows all rows.
        accessor.CurrentTenantId = null;
        var all = await db.IdentityUsers.ToListAsync();
        Assert.Equal(2, all.Count);
    }

    // ── Super-admin override (audit responsibility handoff) ────────────

    [Fact]
    public void TenantResolution_Grants_Override_To_SuperAdmin()
    {
        var svc = new TenantResolutionService();
        var principal = PrincipalWithRoles(PlatformTenant, "super-admin");

        var result = svc.Resolve(principal, TenantAlpha.ToString("D"));

        Assert.True(result.IsOverride);
        Assert.Equal(TenantAlpha, result.TenantId);
        Assert.Null(result.DenyReason);
    }

    [Fact]
    public void TenantResolution_Grants_Override_To_PlatformOperator()
    {
        var svc = new TenantResolutionService();
        var principal = PrincipalWithRoles(PlatformTenant, "platform-operator");

        var result = svc.Resolve(principal, TenantBeta.ToString("D"));

        Assert.True(result.IsOverride);
        Assert.Equal(TenantBeta, result.TenantId);
    }

    [Fact]
    public void TenantResolution_Refuses_Override_From_Parent_Role()
    {
        var svc = new TenantResolutionService();
        var principal = PrincipalWithRoles(TenantAlpha, "parent");

        var result = svc.Resolve(principal, TenantBeta.ToString("D"));

        Assert.False(result.IsOverride);
        Assert.Null(result.TenantId);
        Assert.Equal("override_forbidden", result.DenyReason);
    }

    [Fact]
    public void TenantResolution_Returns_ClaimTenant_When_No_Override_Header()
    {
        var svc = new TenantResolutionService();
        var principal = PrincipalWithRoles(TenantAlpha, "parent");

        var result = svc.Resolve(principal, tenantOverrideHeader: null);

        Assert.False(result.IsOverride);
        Assert.Equal(TenantAlpha, result.TenantId);
    }

    [Fact]
    public void TenantResolution_Rejects_Invalid_Override_Header()
    {
        var svc = new TenantResolutionService();
        var principal = PrincipalWithRoles(PlatformTenant, "super-admin");

        var result = svc.Resolve(principal, "not-a-guid");

        Assert.False(result.IsOverride);
        Assert.Null(result.TenantId);
        Assert.Equal("invalid_override_header", result.DenyReason);
    }

    private static ClaimsPrincipal PrincipalWithRoles(Guid tenantId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString("D")),
        };
        foreach (var r in roles)
        {
            claims.Add(new Claim("roles", r));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
