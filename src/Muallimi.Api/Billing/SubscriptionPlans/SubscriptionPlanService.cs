using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Billing.SubscriptionPlans;

/// <summary>
/// T033 + T037 — Operator-facing CRUD for SubscriptionPlan. Plan type/tier/billing
/// cycle combination is unique (enforced by the DbContext index). Pricing is
/// stored in the entity; formatting lives in the API layer.
/// </summary>
public sealed record SubscriptionPlanInput(
    string PlanNameAr,
    string PlanNameEn,
    string PlanType,
    string Tier,
    decimal PriceEgp,
    decimal? PriceUsd,
    string BillingCycle,
    int? SeatLimit,
    string FeatureEntitlementsJson,
    string UsageLimitsJson);

public sealed record SubscriptionPlanPricingUpdate(decimal PriceEgp, decimal? PriceUsd);

public interface ISubscriptionPlanService
{
    Task<SubscriptionPlan> CreateAsync(SubscriptionPlanInput input, Guid operatorId, CancellationToken ct = default);
    Task<SubscriptionPlan?> UpdatePricingAsync(Guid planId, SubscriptionPlanPricingUpdate update, CancellationToken ct = default);
    Task<SubscriptionPlan?> SetActiveAsync(Guid planId, bool isActive, CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionPlan>> ListAsync(string? planType, bool includeInactive, CancellationToken ct = default);
    Task<SubscriptionPlan?> GetAsync(Guid planId, CancellationToken ct = default);
}

public sealed class SubscriptionPlanService : ISubscriptionPlanService
{
    private readonly MuallimiDbContext _db;

    public SubscriptionPlanService(MuallimiDbContext db) => _db = db;

    public async Task<SubscriptionPlan> CreateAsync(SubscriptionPlanInput input, Guid operatorId, CancellationToken ct = default)
    {
        ValidateInput(input);
        var now = DateTime.UtcNow;
        var plan = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(),
            PlanNameAr = input.PlanNameAr.Trim(),
            PlanNameEn = input.PlanNameEn.Trim(),
            PlanType = input.PlanType,
            Tier = input.Tier,
            PriceEgp = input.PriceEgp,
            PriceUsd = input.PriceUsd,
            BillingCycle = input.BillingCycle,
            SeatLimit = input.SeatLimit,
            FeatureEntitlements = input.FeatureEntitlementsJson,
            UsageLimits = input.UsageLimitsJson,
            IsActive = true,
            CreatedByOperatorId = operatorId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.SubscriptionPlans.Add(plan);
        await _db.SaveChangesAsync(ct);
        return plan;
    }

    public async Task<SubscriptionPlan?> UpdatePricingAsync(Guid planId, SubscriptionPlanPricingUpdate update, CancellationToken ct = default)
    {
        var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.PlanId == planId, ct);
        if (plan is null) return null;
        if (update.PriceEgp < 0) throw new ArgumentOutOfRangeException(nameof(update), "price_egp must be >= 0");
        plan.PriceEgp = update.PriceEgp;
        plan.PriceUsd = update.PriceUsd;
        plan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return plan;
    }

    public async Task<SubscriptionPlan?> SetActiveAsync(Guid planId, bool isActive, CancellationToken ct = default)
    {
        var plan = await _db.SubscriptionPlans.FirstOrDefaultAsync(p => p.PlanId == planId, ct);
        if (plan is null) return null;
        plan.IsActive = isActive;
        plan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return plan;
    }

    public async Task<IReadOnlyList<SubscriptionPlan>> ListAsync(string? planType, bool includeInactive, CancellationToken ct = default)
    {
        var q = _db.SubscriptionPlans.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(planType)) q = q.Where(p => p.PlanType == planType);
        if (!includeInactive) q = q.Where(p => p.IsActive);
        return await q.OrderBy(p => p.PlanType).ThenBy(p => p.Tier).ThenBy(p => p.BillingCycle).ToListAsync(ct);
    }

    public Task<SubscriptionPlan?> GetAsync(Guid planId, CancellationToken ct = default)
        => _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == planId, ct);

    private static void ValidateInput(SubscriptionPlanInput input)
    {
        if (string.IsNullOrWhiteSpace(input.PlanNameAr) || string.IsNullOrWhiteSpace(input.PlanNameEn))
            throw new ArgumentException("plan_name_ar and plan_name_en are required", nameof(input));
        if (input.PlanType is not ("family" or "school"))
            throw new ArgumentException("plan_type must be family or school", nameof(input));
        if (input.Tier is not ("free" or "standard" or "premium"))
            throw new ArgumentException("tier must be free, standard or premium", nameof(input));
        if (input.BillingCycle is not ("monthly" or "yearly"))
            throw new ArgumentException("billing_cycle must be monthly or yearly", nameof(input));
        if (input.PriceEgp < 0)
            throw new ArgumentException("price_egp must be >= 0", nameof(input));
        AssertJson(input.FeatureEntitlementsJson, nameof(input.FeatureEntitlementsJson));
        AssertJson(input.UsageLimitsJson, nameof(input.UsageLimitsJson));
    }

    private static void AssertJson(string json, string paramName)
    {
        try { using var _ = JsonDocument.Parse(json); }
        catch { throw new ArgumentException($"{paramName} must be valid JSON", paramName); }
    }
}
