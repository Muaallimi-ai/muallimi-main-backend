using System;
using System.Collections.Generic;
using Muallimi.Domain.Identity;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.Domain.Tests.Identity;

/// <summary>
/// T058 — Domain tests for <see cref="UserRoleInvariants"/>.
///
/// Covers:
///   • role-scope matching — Family roles only on Family tenants,
///     School roles only on School tenants, Platform roles only on the
///     Platform tenant, <see cref="RoleScope.Any"/> allowed anywhere.
///   • last-super-admin protection — the final active super-admin grant
///     cannot be revoked (FR-015).
/// </summary>
public class UserRoleScopeInvariantTests
{
    // ── Role-scope matching ────────────────────────────────────────────

    [Theory]
    [InlineData(RoleScope.Platform, TenantType.Platform, true)]
    [InlineData(RoleScope.School, TenantType.School, true)]
    [InlineData(RoleScope.Family, TenantType.Family, true)]
    [InlineData(RoleScope.Any, TenantType.Platform, true)]
    [InlineData(RoleScope.Any, TenantType.School, true)]
    [InlineData(RoleScope.Any, TenantType.Family, true)]
    [InlineData(RoleScope.Platform, TenantType.School, false)]
    [InlineData(RoleScope.Platform, TenantType.Family, false)]
    [InlineData(RoleScope.School, TenantType.Platform, false)]
    [InlineData(RoleScope.School, TenantType.Family, false)]
    [InlineData(RoleScope.Family, TenantType.Platform, false)]
    [InlineData(RoleScope.Family, TenantType.School, false)]
    public void IsScopeAllowed_Respects_Matrix(
        RoleScope roleScope,
        TenantType tenantType,
        bool expected)
    {
        Assert.Equal(expected, UserRoleInvariants.IsScopeAllowed(roleScope, tenantType));
    }

    // ── Last-super-admin protection ────────────────────────────────────

    private static UserRole BuildActiveGrant(Guid id) => new()
    {
        Id = id,
        UserId = Guid.NewGuid(),
        RoleId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        GrantedBy = Guid.NewGuid(),
        GrantedAt = DateTime.UtcNow,
        RevokedAt = null,
    };

    [Fact]
    public void WouldLeaveNoSuperAdmin_Returns_True_When_Only_One_Active_Grant()
    {
        var grant = BuildActiveGrant(Guid.NewGuid());
        var leaves = UserRoleInvariants.WouldLeaveNoSuperAdmin(
            new[] { grant },
            grant.Id);
        Assert.True(leaves);
    }

    [Fact]
    public void WouldLeaveNoSuperAdmin_Returns_False_When_Other_Active_Grant_Remains()
    {
        var target = BuildActiveGrant(Guid.NewGuid());
        var other = BuildActiveGrant(Guid.NewGuid());
        var leaves = UserRoleInvariants.WouldLeaveNoSuperAdmin(
            new[] { target, other },
            target.Id);
        Assert.False(leaves);
    }

    [Fact]
    public void WouldLeaveNoSuperAdmin_Ignores_Already_Revoked_Grants()
    {
        var target = BuildActiveGrant(Guid.NewGuid());
        var revoked = BuildActiveGrant(Guid.NewGuid());
        revoked.RevokedAt = DateTime.UtcNow.AddMinutes(-5);

        var leaves = UserRoleInvariants.WouldLeaveNoSuperAdmin(
            new[] { target, revoked },
            target.Id);

        // Only `target` was active; revoking it leaves zero.
        Assert.True(leaves);
    }

    [Fact]
    public void WouldLeaveNoSuperAdmin_Handles_Empty_List()
    {
        var leaves = UserRoleInvariants.WouldLeaveNoSuperAdmin(
            Array.Empty<UserRole>(),
            Guid.NewGuid());
        Assert.True(leaves);
    }

    [Fact]
    public void Revoking_Non_Last_Super_Admin_Is_Allowed()
    {
        var a = BuildActiveGrant(Guid.NewGuid());
        var b = BuildActiveGrant(Guid.NewGuid());
        var c = BuildActiveGrant(Guid.NewGuid());
        var active = new List<UserRole> { a, b, c };

        foreach (var g in active)
        {
            Assert.False(UserRoleInvariants.WouldLeaveNoSuperAdmin(active, g.Id));
        }
    }

    [Fact]
    public void SuperAdminRoleName_Is_Stable()
    {
        // Stability pin: role seeder relies on this literal.
        Assert.Equal("super-admin", UserRoleInvariants.SuperAdminRoleName);
    }
}
