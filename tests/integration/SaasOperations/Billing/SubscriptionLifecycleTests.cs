using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Billing.SubscriptionLifecycle;
using Muallimi.Api.Billing.SubscriptionPlans;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Billing;

public class SubscriptionLifecycleTests
{
    private static (ISubscriptionLifecycleService service, MuallimiDbContext db) Build()
    {
        var db = Phase6TestDbContextFactory.Create();
        var outbox = new Phase6OperationalEventOutbox(db);
        var audit = new AuditTrailWriter(db);
        var svc = new SubscriptionLifecycleService(db, outbox, audit);
        return (svc, db);
    }

    private static async Task<SubscriptionPlan> SeedPlanAsync(MuallimiDbContext db, decimal price = 100m, string tier = "standard", string cycle = "monthly")
    {
        var plan = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(),
            PlanNameAr = "خطة معيارية",
            PlanNameEn = "Standard",
            PlanType = "family",
            Tier = tier,
            PriceEgp = price,
            BillingCycle = cycle,
            FeatureEntitlements = "{}",
            UsageLimits = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan;
    }

    [Fact]
    public async Task CreateAsync_starts_in_trial_and_emits_event()
    {
        var (svc, db) = Build();
        var plan = await SeedPlanAsync(db);
        var tenantId = Guid.NewGuid();

        var sub = await svc.CreateAsync(new SubscriptionCreateInput(
            tenantId, plan.PlanId, "pm_test", "corr-1", InitialStatus: "trial"));

        Assert.Equal("trial", sub.Status);
        Assert.NotNull(sub.TrialEnd);
        Assert.Single(db.Phase6OperationalEvents, e => e.EventKind == "subscription_created");
        Assert.Single(db.AuditEntries, a => a.ActionType == "subscription.created");
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_subscription()
    {
        var (svc, db) = Build();
        var plan = await SeedPlanAsync(db);
        var tenantId = Guid.NewGuid();
        // First create a non-terminal active subscription (trial). The
        // second call must be rejected because pending_payment is the
        // ONLY state that gets replaced silently.
        await svc.CreateAsync(new SubscriptionCreateInput(
            tenantId, plan.PlanId, "pm_test", "corr-1", InitialStatus: "trial"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CreateAsync(new SubscriptionCreateInput(
                tenantId, plan.PlanId, "pm_test", "corr-2", InitialStatus: "trial")));
    }

    [Fact]
    public async Task ChangePlanAsync_upgrade_is_immediate_with_proration()
    {
        var (svc, db) = Build();
        var standard = await SeedPlanAsync(db, price: 100m);
        var premium = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(),
            PlanNameAr = "بريميوم",
            PlanNameEn = "Premium",
            PlanType = "family",
            Tier = "premium",
            PriceEgp = 200m,
            BillingCycle = "monthly",
            FeatureEntitlements = "{}",
            UsageLimits = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.SubscriptionPlans.Add(premium);
        await db.SaveChangesAsync();

        var tenantId = Guid.NewGuid();
        await svc.CreateAsync(new SubscriptionCreateInput(tenantId, standard.PlanId, "pm_test", "corr-1"));

        var result = await svc.ChangePlanAsync(tenantId, premium.PlanId, "corr-2");

        Assert.NotNull(result);
        Assert.Equal(premium.PlanId, result!.NewPlanId);
        Assert.NotNull(result.ProratedChargeEgp);
        Assert.Contains(db.AuditEntries, a => a.ActionType == "subscription.upgraded");
    }

    [Fact]
    public async Task CancelAsync_sets_effective_at_period_end()
    {
        var (svc, db) = Build();
        var plan = await SeedPlanAsync(db);
        var tenantId = Guid.NewGuid();
        var created = await svc.CreateAsync(new SubscriptionCreateInput(tenantId, plan.PlanId, "pm_test", "corr-1"));

        var result = await svc.CancelAsync(tenantId, "too-expensive", "corr-2");

        Assert.NotNull(result);
        Assert.Equal(created.CurrentPeriodEnd, result!.EffectiveAt);
        var reloaded = await db.Subscriptions.FirstAsync(s => s.TenantId == tenantId);
        Assert.Equal("cancelled", reloaded.Status);
    }

    [Fact]
    public async Task EnterGraceAsync_sets_grace_window_and_emits_event()
    {
        var (svc, db) = Build();
        var plan = await SeedPlanAsync(db);
        var tenantId = Guid.NewGuid();
        var sub = await svc.CreateAsync(new SubscriptionCreateInput(tenantId, plan.PlanId, "pm_test", "corr-1"));

        // Promote to active first (skip trial) — then enter grace.
        var tracked = await db.Subscriptions.FirstAsync(s => s.SubscriptionId == sub.SubscriptionId);
        tracked.Status = "active";
        await db.SaveChangesAsync();

        var graced = await svc.EnterGraceAsync(sub.SubscriptionId, "corr-grace");

        Assert.NotNull(graced);
        Assert.Equal("grace", graced!.Status);
        Assert.NotNull(graced.GracePeriodEnd);
        Assert.Contains(db.Phase6OperationalEvents, e => e.EventKind == "subscription_entered_grace");
    }
}
