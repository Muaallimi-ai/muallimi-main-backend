using System;

namespace Muallimi.Domain.Shared;

/// <summary>
/// Marker interface for every tenant-scoped entity across the platform.
/// EF Core applies a global query filter on <c>TenantId</c> for anything
/// implementing this interface so cross-tenant reads are blocked by
/// default (see <c>Muallimi.Infrastructure.Persistence</c> for the
/// filter binding, and each module's per-surface TenantQueryFilter for
/// the authorization wrapper).
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}
