using System;
using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// UserRole — grant edge (user × role × tenant). Uniqueness is enforced
/// by the EF configuration: one active grant per (UserId, RoleId, TenantId)
/// where <c>RevokedAt IS NULL</c>. The role-scope invariant (role scope
/// must match tenant type) is enforced by the UserManagementService at
/// grant time — not at the row level — so that legacy migrations can be
/// idempotent.
/// </summary>
public class UserRole : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid TenantId { get; set; }
    public Guid GrantedBy { get; set; }
    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    public void Revoke()
    {
        if (RevokedAt is not null)
        {
            throw new InvalidOperationException("Grant already revoked.");
        }
        RevokedAt = DateTime.UtcNow;
    }
}
