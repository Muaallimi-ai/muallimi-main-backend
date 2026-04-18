using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Billing.SubscriptionLifecycle;

/// <summary>
/// T043 — Phase 5 licence sync. When a school tenant subscribes or changes plan,
/// the corresponding SchoolLicense row receives the new seat_limit,
/// feature_gates, and subscription_end derived from the SubscriptionPlan.
/// </summary>
public interface IPhase5LicenseSyncService
{
    Task SyncFromSubscriptionAsync(Guid schoolTenantId, Guid subscriptionId, CancellationToken ct = default);
}

public sealed class Phase5LicenseSyncService : IPhase5LicenseSyncService
{
    private readonly MuallimiDbContext _db;
    private readonly ILogger<Phase5LicenseSyncService> _logger;

    public Phase5LicenseSyncService(MuallimiDbContext db, ILogger<Phase5LicenseSyncService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task SyncFromSubscriptionAsync(Guid schoolTenantId, Guid subscriptionId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct);
        if (subscription is null) { _logger.LogWarning("Subscription {Id} missing — license sync skipped", subscriptionId); return; }
        if (subscription.PlanType != "school") return;

        var plan = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == subscription.PlanId, ct);
        if (plan is null) return;

        var license = await _db.Set<SchoolLicense>()
            .FirstOrDefaultAsync(l => l.SchoolTenantId == schoolTenantId, ct);
        if (license is null)
        {
            _logger.LogWarning("No SchoolLicense for tenant {Id} — Phase 5 bootstrap required", schoolTenantId);
            return;
        }

        license.PlanTier = plan.Tier;
        license.SeatLimit = plan.SeatLimit ?? license.SeatLimit;
        license.FeatureGates = plan.FeatureEntitlements;
        license.SubscriptionEnd = subscription.CurrentPeriodEnd;
        license.IsTrial = subscription.Status == "trial";
        license.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
