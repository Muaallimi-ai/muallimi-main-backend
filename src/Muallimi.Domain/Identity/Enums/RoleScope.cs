namespace Muallimi.Domain.Identity.Enums;

/// <summary>
/// Scope at which a role is valid. The role-scope invariant
/// (<c>UserRole</c>) enforces that <c>Role.Scope</c> matches
/// <c>Tenant.Type</c> — <c>Platform</c> roles only on the Platform tenant,
/// <c>School</c> roles only on School tenants, <c>Family</c> roles only on
/// Family tenants. <c>Any</c> is reserved for future cross-scope roles
/// (none seeded in the 8-role baseline).
/// </summary>
public enum RoleScope
{
    Family = 1,
    School = 2,
    Platform = 3,
    Any = 99,
}
