using System;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// Tenant — boundary of data ownership and authorization scoping.
/// Invariants:
///   - Exactly one <c>Platform</c> tenant exists (singleton);
///     cannot be deleted.
///   - <c>Family</c> tenants are created on parent self-registration.
///   - <c>School</c> tenants are created via super-admin onboarding.
///   - <c>Archived</c> is terminal — no transitions back.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; }
    public TenantType Type { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Locale { get; set; } = "ar";
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public string Metadata { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public void Suspend()
    {
        if (Status == TenantStatus.Archived)
        {
            throw new InvalidOperationException("Archived tenant cannot be suspended.");
        }
        Status = TenantStatus.Suspended;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Unsuspend()
    {
        if (Status != TenantStatus.Suspended)
        {
            throw new InvalidOperationException($"Only Suspended tenants can be unsuspended (current: {Status}).");
        }
        Status = TenantStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Archive()
    {
        if (Type == TenantType.Platform)
        {
            throw new InvalidOperationException("The Platform tenant cannot be archived.");
        }
        Status = TenantStatus.Archived;
        DeletedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
