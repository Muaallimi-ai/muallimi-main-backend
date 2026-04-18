using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SaasOperations;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.OperatorManagement.TenantHealth;

/// <summary>
/// T100 — Rolls up tenant health indicators (subscription status, active
/// students, session counts, AI cost, storage, engagement, at-risk) into the
/// TenantHealthView table for the operator dashboard. Invoked by the periodic
/// TenantHealthViewUpdater and by the on-demand refresh endpoint.
/// </summary>
public sealed class TenantHealthRollupService
{
    private readonly MuallimiDbContext _db;

    public TenantHealthRollupService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var tenantIds = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .Select(s => s.TenantId)
            .Union(_db.Subscriptions.IgnoreQueryFilters().Select(s => s.TenantId))
            .Union(_db.SchoolTenants.IgnoreQueryFilters().Select(s => s.TenantId))
            .Distinct()
            .ToListAsync(ct);

        foreach (var tenantId in tenantIds)
        {
            await RefreshAsync(tenantId, ct);
        }
    }

    public async Task<TenantHealthView> RefreshAsync(Guid tenantId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var monthStart = now.AddDays(-30);

        var sub = await _db.Subscriptions
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        SubscriptionPlan? plan = null;
        if (sub is not null)
        {
            plan = await _db.SubscriptionPlans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlanId == sub.PlanId, ct);
        }

        var school = await _db.SchoolTenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

        var tenantType = school is not null ? "school" : "family";

        var activeStudents = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == tenantId, ct);

        var monthlySessions = await _db.Set<StudentSession>()
            .IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == tenantId && s.SessionStartedAt >= monthStart, ct);

        var monthlyAiCost = await _db.Phase6AIOperationsMetrics
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.OccurredAt >= monthStart)
            .Select(m => (decimal?)m.EstimatedCostEgp)
            .SumAsync(ct) ?? 0m;

        var atRisk = await _db.Set<AtRiskFlag>()
            .IgnoreQueryFilters()
            .CountAsync(f => f.TenantId == tenantId && f.ClearedAt == null, ct);

        var lastActivity = await _db.Set<StudentSession>()
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .MaxAsync(s => (DateTime?)s.SessionLastActivityAt, ct);

        // Engagement score: average mastery across any school aggregate rows
        // if present, otherwise null.
        decimal? engagement = null;
        var aggregates = await _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.ScopeType == "school")
            .Select(a => (decimal?)a.AverageMastery)
            .ToListAsync(ct);
        if (aggregates.Count > 0)
        {
            engagement = Math.Round(aggregates.Average() ?? 0m, 2);
        }

        var existing = await _db.TenantHealthViews
            .FirstOrDefaultAsync(v => v.TenantId == tenantId, ct);

        var subscriptionStatus = sub?.Status ?? "none";
        var planTier = plan?.Tier ?? "free";

        if (existing is null)
        {
            existing = new TenantHealthView
            {
                TenantHealthId = Guid.NewGuid(),
                TenantId = tenantId,
            };
            _db.TenantHealthViews.Add(existing);
        }

        existing.TenantType = tenantType;
        existing.SubscriptionStatus = subscriptionStatus;
        existing.PlanTier = planTier;
        existing.ActiveStudentCount = activeStudents;
        existing.MonthlySessionCount = monthlySessions;
        existing.MonthlyAiCostEgp = Math.Round(monthlyAiCost, 4);
        existing.StorageUsageMb = 0;
        existing.EngagementScore = engagement;
        existing.AtRiskStudentCount = atRisk;
        existing.LastActivityAt = lastActivity;
        existing.ComputedAt = now;

        await _db.SaveChangesAsync(ct);
        return existing;
    }
}
