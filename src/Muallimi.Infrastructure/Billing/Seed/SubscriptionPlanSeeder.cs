using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Billing.Seed;

/// <summary>
/// Idempotent seeder for the 3 canonical family subscription plans.
/// Mirrors the hardcoded plans in the registration PlanSelectionStep so
/// the UI can read from the DB instead of a hardcoded const. Runs on
/// startup; bails out if any family plans already exist.
///
/// Prices and feature sets are the canonical source of truth —
/// frontend hardcoded copies should migrate to GET /api/v1/billing/plans.
/// </summary>
public sealed class SubscriptionPlanSeeder
{
    private readonly MuallimiDbContext _db;

    public SubscriptionPlanSeeder(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<int> EnsureSeededAsync(CancellationToken ct = default)
    {
        if (await _db.SubscriptionPlans.AnyAsync(p => p.PlanType == "family", ct).ConfigureAwait(false))
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        // created_by_operator_id has no FK — Guid.Empty marks "system seeder".
        var op = Guid.Empty;

        var plans = new[]
        {
            new SubscriptionPlan
            {
                PlanId = Guid.NewGuid(),
                PlanNameAr = "الأساسية",
                PlanNameEn = "Basic",
                PlanType = "family",
                Tier = "basic",
                PriceEgp = 349m,
                PriceUsd = null,
                BillingCycle = "monthly",
                SeatLimit = 1,
                FeatureEntitlements = """{"daily_voice_minutes":20,"all_subjects":true,"ai_instant_answers":true,"image_analysis":true,"bilingual":true,"parent_dashboard":false,"weekly_reports":false}""",
                UsageLimits = """{"daily_voice_minutes":20}""",
                IsActive = true,
                CreatedByOperatorId = op,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SubscriptionPlan
            {
                PlanId = Guid.NewGuid(),
                PlanNameAr = "الذكية",
                PlanNameEn = "Smart",
                PlanType = "family",
                Tier = "smart",
                PriceEgp = 799m,
                PriceUsd = null,
                BillingCycle = "monthly",
                SeatLimit = 1,
                FeatureEntitlements = """{"daily_voice_minutes":45,"all_subjects":true,"ai_instant_answers":true,"image_analysis":true,"bilingual":true,"question_rewriting":true,"socratic_teaching":true,"progress_tracking":true,"parent_dashboard":true,"weekly_reports":true}""",
                UsageLimits = """{"daily_voice_minutes":45}""",
                IsActive = true,
                CreatedByOperatorId = op,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new SubscriptionPlan
            {
                PlanId = Guid.NewGuid(),
                PlanNameAr = "العائلية",
                PlanNameEn = "Family",
                PlanType = "family",
                Tier = "family",
                PriceEgp = 1999m,
                PriceUsd = null,
                BillingCycle = "monthly",
                SeatLimit = 3,
                FeatureEntitlements = """{"daily_voice_minutes":45,"all_subjects":true,"ai_instant_answers":true,"image_analysis":true,"bilingual":true,"question_rewriting":true,"socratic_teaching":true,"progress_tracking":true,"parent_dashboard":true,"weekly_reports":true,"unified_family_dashboard":true,"comparative_reports":true,"priority_support":true}""",
                UsageLimits = """{"daily_voice_minutes":45}""",
                IsActive = true,
                CreatedByOperatorId = op,
                CreatedAt = now,
                UpdatedAt = now,
            },
        };

        foreach (var plan in plans)
        {
            _db.SubscriptionPlans.Add(plan);
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return plans.Length;
    }
}
