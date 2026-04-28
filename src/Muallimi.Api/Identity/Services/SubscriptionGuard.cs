using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// Add-child redesign Phase 7.3 — block-on-login subscription gate.
///
/// Active sessions are NEVER terminated by this guard (per the spec).
/// The gate only fires on entry-point auth flows: <c>/login</c>,
/// <c>/login/pin</c>, <c>/refresh</c>, and
/// <c>/parent/switch-to-child</c>. When a Family tenant's subscription
/// is <c>expired</c> or <c>cancelled</c>, those endpoints return 402
/// with <c>{ code: "subscription_expired", renewalUrl: "/parent/subscription" }</c>.
///
/// Tenants without a subscription row (e.g. schools, fresh family
/// registrations whose payment is still pending in another flow) and
/// platform/operator tenants are always allowed — they have separate
/// billing semantics or none at all.
/// </summary>
public interface ISubscriptionGuard
{
    Task<SubscriptionGuardResult> CheckActiveAsync(Guid tenantId, CancellationToken ct = default);
}

public sealed record SubscriptionGuardResult(bool Allowed, string? RenewalUrl = null);

public sealed class SubscriptionGuard : ISubscriptionGuard
{
    public const string RenewalPath = "/parent/subscription";

    private readonly MuallimiDbContext _db;

    public SubscriptionGuard(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<SubscriptionGuardResult> CheckActiveAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return new SubscriptionGuardResult(true);

        // Only Family tenants are gated by this guard. Schools and the
        // Platform tenant follow different billing rules.
        var tenant = await _db.IdentityTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Type != TenantType.Family)
        {
            return new SubscriptionGuardResult(true);
        }

        // Look at the tenant's most recent subscription. Status
        // 'expired' or 'cancelled' blocks new auth entry. 'trial',
        // 'active', 'grace' are allowed.
        var status = await _db.Subscriptions.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Status)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(status)) return new SubscriptionGuardResult(true);

        if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return new SubscriptionGuardResult(false, RenewalPath);
        }
        return new SubscriptionGuardResult(true);
    }
}
