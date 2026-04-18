using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Content;
using Muallimi.Domain.Coverage;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Coverage;

/// <summary>
/// T115 - Aggregates per-lesson/per-asset-type coverage state into the dashboard
/// payload consumed by GET /admin/content/coverage. Filters are optional and
/// compose: curriculum type, grade, and subject.
///
/// Strategy: hydrate the filtered lessons plus their related asset/job/review
/// rows in three queries, then derive state in memory via
/// <see cref="CoverageStatusProjection"/>. That keeps the projection rules in a
/// single place and avoids translating the state-machine decisions into LINQ.
/// </summary>
public class CoverageAggregator
{
    private readonly MuallimiDbContext _db;

    public CoverageAggregator(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<CoverageDashboard> BuildAsync(CoverageFilters filters, DateTime now, CancellationToken ct = default)
    {
        var lessonQuery = _db.Lessons.AsNoTracking();
        if (filters.CurriculumType is { } ct1)
            lessonQuery = lessonQuery.Where(l => l.CurriculumType == ct1);
        if (filters.Grade is { } g)
            lessonQuery = lessonQuery.Where(l => l.Grade == g);
        if (filters.Subject is { } s)
            lessonQuery = lessonQuery.Where(l => l.Subject == s);

        var lessons = await lessonQuery
            .OrderBy(l => l.CurriculumType)
            .ThenBy(l => l.Subject)
            .ThenBy(l => l.Path)
            .ToListAsync(ct);

        if (lessons.Count == 0)
        {
            return CoverageDashboard.Empty(filters);
        }

        var lessonIds = lessons.Select(l => l.LessonId).ToHashSet();

        var assets = await _db.GeneratedAssets.AsNoTracking()
            .Where(a => lessonIds.Contains(a.LessonId))
            .ToListAsync(ct);

        var jobs = await _db.GenerationJobs.AsNoTracking()
            .Where(j => lessonIds.Contains(j.LessonId))
            .ToListAsync(ct);

        var published = await _db.PublishedAssets.AsNoTracking()
            .Where(p => lessonIds.Contains(p.LessonId))
            .ToListAsync(ct);

        var assetIds = assets.Select(a => a.AssetId).ToHashSet();
        var assignments = assetIds.Count == 0
            ? new List<ReviewAssignment>()
            : await _db.ReviewAssignments.AsNoTracking()
                .Where(r => assetIds.Contains(r.AssetId))
                .ToListAsync(ct);

        return BuildDashboard(lessons, assets, jobs, published, assignments, filters, now);
    }

    /// <summary>
    /// In-memory aggregation — exposed so integration tests can drive the
    /// pipeline with seeded state without EF plumbing.
    /// </summary>
    public static CoverageDashboard BuildDashboard(
        IReadOnlyList<Lesson> lessons,
        IReadOnlyList<GeneratedAsset> assets,
        IReadOnlyList<GenerationJob> jobs,
        IReadOnlyList<PublishedAsset> published,
        IReadOnlyList<ReviewAssignment> assignments,
        CoverageFilters filters,
        DateTime now)
    {
        var lessonRows = new List<CoverageLessonRow>(lessons.Count);
        var stateTotals = new Dictionary<CoverageState, int>();
        foreach (var state in Enum.GetValues<CoverageState>())
            stateTotals[state] = 0;

        var assetTypeTotalsMutable = new Dictionary<AssetType, Dictionary<CoverageState, int>>();
        foreach (var at in CoverageStatusProjection.TrackedAssetTypes)
        {
            assetTypeTotalsMutable[at] = new Dictionary<CoverageState, int>();
            foreach (var state in Enum.GetValues<CoverageState>())
                assetTypeTotalsMutable[at][state] = 0;
        }

        int slaBreachedCount = 0;

        foreach (var lesson in lessons)
        {
            var statuses = CoverageStatusProjection.Derive(
                lesson.LessonId, assets, jobs, published, assignments, now);

            var assetRows = new List<CoverageAssetRow>(statuses.Count);
            foreach (var status in statuses)
            {
                var anchor = status.QueueAge is null
                    ? (DateTime?)null
                    : status.LastUpdatedAt;
                var slaBreached = status.State == CoverageState.PendingReview
                    && QueueAgeCalculator.IsSlaBreached(status.AssetType, anchor, now);
                if (slaBreached) slaBreachedCount++;

                var ageBusinessDays = QueueAgeCalculator.BusinessDayAge(anchor, now);

                assetRows.Add(new CoverageAssetRow(
                    AssetType: status.AssetType,
                    State: status.State,
                    QueueAgeBusinessDays: ageBusinessDays,
                    SlaThresholdBusinessDays: QueueAgeCalculator.SlaThresholdBusinessDays(status.AssetType),
                    SlaBreached: slaBreached,
                    Owner: status.Owner,
                    LastUpdatedAt: status.LastUpdatedAt));

                stateTotals[status.State]++;
                assetTypeTotalsMutable[status.AssetType][status.State]++;
            }

            lessonRows.Add(new CoverageLessonRow(
                LessonId: lesson.LessonId,
                CurriculumType: lesson.CurriculumType,
                Grade: lesson.Grade,
                Subject: lesson.Subject,
                Path: lesson.Path,
                Assets: assetRows));
        }

        var assetTypeTotals = assetTypeTotalsMutable.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<CoverageState, int>)kv.Value);

        return new CoverageDashboard(
            Filters: filters,
            TotalLessons: lessons.Count,
            StateTotals: stateTotals,
            AssetTypeTotals: assetTypeTotals,
            SlaBreachedCount: slaBreachedCount,
            Lessons: lessonRows);
    }
}

public record CoverageFilters(
    CurriculumType? CurriculumType,
    Grade? Grade,
    Subject? Subject);

public record CoverageAssetRow(
    AssetType AssetType,
    CoverageState State,
    int QueueAgeBusinessDays,
    int SlaThresholdBusinessDays,
    bool SlaBreached,
    string? Owner,
    DateTime LastUpdatedAt);

public record CoverageLessonRow(
    Guid LessonId,
    CurriculumType CurriculumType,
    Grade Grade,
    Subject Subject,
    string Path,
    IReadOnlyList<CoverageAssetRow> Assets);

public record CoverageDashboard(
    CoverageFilters Filters,
    int TotalLessons,
    IReadOnlyDictionary<CoverageState, int> StateTotals,
    IReadOnlyDictionary<AssetType, IReadOnlyDictionary<CoverageState, int>> AssetTypeTotals,
    int SlaBreachedCount,
    IReadOnlyList<CoverageLessonRow> Lessons)
{
    public static CoverageDashboard Empty(CoverageFilters filters)
    {
        var stateTotals = Enum.GetValues<CoverageState>().ToDictionary(s => s, _ => 0);
        var assetTypeTotals = CoverageStatusProjection.TrackedAssetTypes
            .ToDictionary(
                at => at,
                _ => (IReadOnlyDictionary<CoverageState, int>)Enum.GetValues<CoverageState>().ToDictionary(s => s, _ => 0));
        return new CoverageDashboard(filters, 0, stateTotals, assetTypeTotals, 0, Array.Empty<CoverageLessonRow>());
    }
}
