using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.PlanGating;

/// <summary>
/// T011 — PlanGateResolver. Reads <see cref="PlanGatePolicy"/> rows and
/// decides whether a given (mode, tenant, plan_tier, subject, grade) can
/// proceed. Uses an in-memory cache-aside with a short TTL so UI calls to
/// <c>/student/plan-gate/snapshot</c> don't hit the DB on every frame while
/// still picking up policy changes within one minute.
///
/// In production the cache is served by Azure Cache for Redis; for Phase 3
/// local parity the in-process <see cref="ConcurrentDictionary"/> keeps us
/// zero-dependency and is trivially swappable.
///
/// Backend re-check rule (constitution + contract): every gated entry point
/// MUST call <see cref="EvaluateAsync"/> on start and on mode transition —
/// the UI resolver's decision is advisory only.
/// </summary>
public record PlanGateContext(
    string Mode,
    Guid? TenantId,
    string PlanTier,
    Guid? SubjectId,
    string? Grade);

public record PlanGateDecision(
    bool Allowed,
    string? Reason,
    PlanGatePolicy? AppliedPolicy);

public interface IPlanGateResolver
{
    Task<PlanGateDecision> EvaluateAsync(PlanGateContext context, CancellationToken ct = default);
}

public sealed class PlanGateResolver : IPlanGateResolver
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private readonly MuallimiDbContext _db;

    public PlanGateResolver(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<PlanGateDecision> EvaluateAsync(PlanGateContext context, CancellationToken ct = default)
    {
        var policy = await LoadPolicyAsync(context.Mode, context.TenantId, ct);
        if (policy is null)
        {
            // No policy = no gate = allowed by default.
            return new PlanGateDecision(true, null, null);
        }

        if (policy.ExpiresAt is DateTime expiresAt && expiresAt <= DateTime.UtcNow)
        {
            return new PlanGateDecision(true, "policy_expired", policy);
        }

        var requiredTiers = ParseArray(policy.RequiredPlanTiers);
        if (requiredTiers.Count > 0 && !requiredTiers.Contains(context.PlanTier))
        {
            return new PlanGateDecision(false, "plan_tier_not_permitted", policy);
        }

        if (context.SubjectId is Guid sid)
        {
            var subjectScope = ParseArray(policy.SubjectScope);
            if (subjectScope.Count > 0 && !subjectScope.Contains(sid.ToString()))
            {
                return new PlanGateDecision(false, "subject_not_permitted", policy);
            }
        }

        if (!string.IsNullOrEmpty(context.Grade))
        {
            var gradeScope = ParseArray(policy.GradeScope);
            if (gradeScope.Count > 0 && !gradeScope.Contains(context.Grade))
            {
                return new PlanGateDecision(false, "grade_not_permitted", policy);
            }
        }

        return new PlanGateDecision(true, null, policy);
    }

    private async Task<PlanGatePolicy?> LoadPolicyAsync(string mode, Guid? tenantId, CancellationToken ct)
    {
        var cacheKey = $"{mode}|{tenantId?.ToString() ?? "global"}";
        if (Cache.TryGetValue(cacheKey, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            return entry.Policy;
        }

        // Prefer a tenant-scoped override; fall back to global default.
        var policy = await _db.PlanGatePolicies
            .AsNoTracking()
            .Where(p => p.Mode == mode && (p.TenantId == tenantId || p.TenantId == null))
            .OrderByDescending(p => p.TenantId != null) // tenant override beats default
            .ThenByDescending(p => p.EnabledAt)
            .FirstOrDefaultAsync(ct);

        Cache[cacheKey] = new CacheEntry(policy, DateTime.UtcNow.Add(CacheTtl));
        return policy;
    }

    private static HashSet<string> ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>();
        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private record CacheEntry(PlanGatePolicy? Policy, DateTime ExpiresAt);
}

public static class PlanGateServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3PlanGate(this IServiceCollection services)
    {
        services.AddScoped<IPlanGateResolver, PlanGateResolver>();
        return services;
    }
}
