using System.Collections.Generic;
using System.Linq;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Domain.Identity;

/// <summary>
/// Pure-domain invariants around <see cref="UserRole"/> grants. Kept
/// deliberately outside the row-level EF configuration (see
/// <see cref="UserRole"/>'s class-level note) so that legacy migrations
/// can be idempotent — the invariant is checked at grant time by the
/// <c>UserManagementService</c> (US5–US6) and by every endpoint that
/// mutates a role assignment. Contract-tested by
/// <c>tests/Muallimi.Domain.Tests/Identity/UserRoleScopeInvariantTests.cs</c> (T058).
/// </summary>
public static class UserRoleInvariants
{
    public const string SuperAdminRoleName = "super-admin";

    /// <summary>
    /// A <see cref="Role"/> grant is valid on a <see cref="Tenant"/> only
    /// when <see cref="Role.Scope"/> matches <see cref="Tenant.Type"/>.
    /// <see cref="RoleScope.Any"/> is permitted on any tenant type.
    /// </summary>
    public static bool IsScopeAllowed(RoleScope roleScope, TenantType tenantType)
    {
        if (roleScope == RoleScope.Any) return true;
        return (roleScope, tenantType) switch
        {
            (RoleScope.Platform, TenantType.Platform) => true,
            (RoleScope.School, TenantType.School) => true,
            (RoleScope.Family, TenantType.Family) => true,
            _ => false,
        };
    }

    /// <summary>
    /// Last-super-admin protection: the final active <c>super-admin</c>
    /// grant on the Platform tenant MUST NOT be revoked. Returns
    /// <c>true</c> when revoking the grant identified by
    /// <paramref name="targetGrantId"/> would leave zero active
    /// super-admin grants on the platform tenant.
    /// </summary>
    public static bool WouldLeaveNoSuperAdmin(
        IEnumerable<UserRole> activeSuperAdminGrants,
        System.Guid targetGrantId)
    {
        var remaining = activeSuperAdminGrants
            .Where(g => g.RevokedAt is null && g.Id != targetGrantId)
            .Count();
        return remaining == 0;
    }
}
