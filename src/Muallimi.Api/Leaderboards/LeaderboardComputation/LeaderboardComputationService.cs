using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Leaderboards.LeaderboardQuery;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Leaderboards.LeaderboardComputation;

/// <summary>
/// T145 (US7) — Snapshot computation for a single class scope.
///
/// Computes rankings from Phase 4 <see cref="MasteryState"/> for the
/// class's active enrolments, projects display names through the
/// school's privacy mode, and stores a <see cref="LeaderboardSnapshot"/>.
/// Classes where the school-level config or per-class override disables
/// leaderboards are skipped.
/// </summary>
public interface ILeaderboardComputationService
{
    Task<LeaderboardSnapshot?> ComputeClassSnapshotAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        string metric,
        Guid? subjectId,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken ct = default);
}

public sealed class LeaderboardComputationService : ILeaderboardComputationService
{
    private readonly MuallimiDbContext _db;
    private readonly ILeaderboardConfigRepository _config;
    private readonly ILeaderboardSnapshotRepository _snapshots;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public LeaderboardComputationService(
        MuallimiDbContext db,
        ILeaderboardConfigRepository config,
        ILeaderboardSnapshotRepository snapshots)
    {
        _db = db;
        _config = config;
        _snapshots = snapshots;
    }

    public async Task<LeaderboardSnapshot?> ComputeClassSnapshotAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        string metric,
        Guid? subjectId,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken ct = default)
    {
        var config = await _config.GetOrDefaultAsync(tenantId, schoolTenantId, ct);
        if (!config.LeaderboardEnabled || !IsClassEnabled(config, classGroupId))
        {
            return null;
        }

        var enrolments = await _db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.ClassGroupId == classGroupId && e.Status == "active")
            .Select(e => e.StudentId)
            .ToListAsync(ct);

        if (enrolments.Count == 0) return null;

        var students = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && enrolments.Contains(s.Id))
            .ToListAsync(ct);

        var values = await ComputeMetricAsync(tenantId, metric, enrolments, subjectId, windowStart, windowEnd, ct);

        var rankedEntries = LeaderboardRankingCalculator
            .Rank(values.Select(v => new RankInput(Key: v.StudentId, Value: v.Value)))
            .Select(r =>
            {
                var student = students.FirstOrDefault(s => s.Id == (Guid)r.Input.Key);
                var realName = student?.DisplayName ?? "Student";
                var projected = LeaderboardPrivacyProjector.Apply(config.PrivacyMode, realName, (Guid)r.Input.Key);
                return new LeaderboardEntryPayload
                {
                    rank = r.Rank,
                    student_id = (Guid)r.Input.Key,
                    display_name = projected,
                    real_display_name = realName,
                    value = r.Input.Value,
                };
            })
            .ToList();

        var snapshot = new LeaderboardSnapshot
        {
            LeaderboardSnapshotId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            ScopeType = "class",
            ScopeId = classGroupId,
            SubjectId = subjectId,
            Metric = metric,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Entries = JsonSerializer.Serialize(rankedEntries, JsonOptions),
            PrivacyMode = config.PrivacyMode,
            ComputedAt = DateTime.UtcNow,
        };

        await _snapshots.UpsertLatestAsync(snapshot, ct);
        await _snapshots.SaveChangesAsync(ct);
        return snapshot;
    }

    private static bool IsClassEnabled(LeaderboardConfig config, Guid classGroupId)
    {
        if (string.IsNullOrWhiteSpace(config.PerClassOverridesJson) || config.PerClassOverridesJson == "[]")
            return true;
        try
        {
            var overrides = JsonSerializer.Deserialize<List<PerClassOverride>>(config.PerClassOverridesJson, JsonOptions);
            if (overrides is null) return true;
            var match = overrides.FirstOrDefault(o => o.class_group_id == classGroupId);
            return match is null ? true : match.enabled;
        }
        catch
        {
            return true;
        }
    }

    private async Task<List<(Guid StudentId, decimal Value)>> ComputeMetricAsync(
        Guid tenantId,
        string metric,
        List<Guid> studentIds,
        Guid? subjectId,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken ct)
    {
        switch (metric)
        {
            case LeaderboardMetrics.Engagement:
                var eventCounts = await _db.ProgressRecords
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(p => p.TenantId == tenantId && studentIds.Contains(p.StudentId)
                        && p.OccurredAt >= windowStart && p.OccurredAt <= windowEnd)
                    .GroupBy(p => p.StudentId)
                    .Select(g => new { g.Key, Count = (decimal)g.Count() })
                    .ToListAsync(ct);
                return studentIds
                    .Select(id => (id, eventCounts.FirstOrDefault(e => e.Key == id)?.Count ?? 0m))
                    .ToList();

            case LeaderboardMetrics.Mastery:
            default:
                var masteryRows = await _db.MasteryStates
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(m => m.TenantId == tenantId && studentIds.Contains(m.StudentId)
                        && (subjectId == null || m.SubjectId == subjectId))
                    .ToListAsync(ct);
                var avgByStudent = masteryRows
                    .GroupBy(m => m.StudentId)
                    .ToDictionary(g => g.Key, g => g.Average(m => m.MasteryScore));
                return studentIds
                    .Select(id => (id, avgByStudent.TryGetValue(id, out var v) ? v : 0m))
                    .ToList();
        }
    }

    private sealed class PerClassOverride
    {
        public Guid class_group_id { get; set; }
        public bool enabled { get; set; }
    }

    public sealed class LeaderboardEntryPayload
    {
        public int rank { get; set; }
        public Guid student_id { get; set; }
        public string display_name { get; set; } = string.Empty;
        public string real_display_name { get; set; } = string.Empty;
        public decimal value { get; set; }
    }
}

public static class LeaderboardMetrics
{
    public const string Mastery = "mastery";
    public const string Engagement = "engagement";
    public const string Improvement = "improvement";

    public static bool IsValid(string metric) =>
        metric == Mastery || metric == Engagement || metric == Improvement;
}

public static class LeaderboardComputationServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5LeaderboardComputationService(this IServiceCollection services)
    {
        services.AddScoped<ILeaderboardComputationService, LeaderboardComputationService>();
        return services;
    }
}
