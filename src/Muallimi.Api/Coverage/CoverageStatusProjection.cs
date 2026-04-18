using Muallimi.Domain.Content;
using Muallimi.Domain.Coverage;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;

namespace Muallimi.Api.Coverage;

/// <summary>
/// T114 - Projects per-lesson, per-asset-type coverage state from the
/// authoritative GeneratedAsset, GenerationJob, ReviewAssignment, and
/// PublishedAsset records. The projection is pure (no DB access) so the
/// aggregator can hydrate it in a single round-trip, and the contract tests
/// can exercise every state without a database.
///
/// State priority per the US6 spec:
///   1. Active PublishedAsset           → Approved
///   2. Rejected / Invalidated / Failed → Failed
///   3. Pending admin/expert review     → PendingReview
///   4. Queued / Producing / AutoValid. → InProduction
///   5. Running / Queued job only       → InProduction
///   6. Nothing                         → NotStarted
///
/// The projection covers the five curriculum asset types required by Phase 1:
///   TextSummary, Audio, Visual, QuizItem, QaCacheEntry.
/// </summary>
public static class CoverageStatusProjection
{
    public static readonly IReadOnlyList<AssetType> TrackedAssetTypes = new[]
    {
        AssetType.TextSummary,
        AssetType.Audio,
        AssetType.Visual,
        AssetType.QuizItem,
        AssetType.QaCacheEntry
    };

    /// <summary>
    /// Derives a CoverageStatus per tracked asset type for a single lesson.
    /// Callers supply the lesson's related aggregate state. Unseen asset
    /// types default to NotStarted so the dashboard always returns a complete
    /// matrix.
    /// </summary>
    public static IReadOnlyList<CoverageStatus> Derive(
        Guid lessonId,
        IEnumerable<GeneratedAsset> assets,
        IEnumerable<GenerationJob> jobs,
        IEnumerable<PublishedAsset> published,
        IEnumerable<ReviewAssignment> assignments,
        DateTime now)
    {
        var assetList = assets.Where(a => a.LessonId == lessonId).ToList();
        var jobList = jobs.Where(j => j.LessonId == lessonId).ToList();
        var publishedList = published.Where(p => p.LessonId == lessonId).ToList();
        var assignmentsByAsset = assignments.ToLookup(a => a.AssetId);

        var result = new List<CoverageStatus>(TrackedAssetTypes.Count);
        foreach (var assetType in TrackedAssetTypes)
        {
            var activePublished = publishedList
                .Where(p => p.AssetType == assetType && p.IsActive)
                .OrderByDescending(p => p.ApprovedAt)
                .FirstOrDefault();

            var latestAsset = assetList
                .Where(a => a.AssetType == assetType)
                .OrderByDescending(a => a.Version)
                .ThenByDescending(a => a.ProducedAt)
                .FirstOrDefault();

            var latestJob = jobList
                .Where(j => JobScopeCovers(j.Scope, assetType))
                .OrderByDescending(j => j.StartedAt ?? DateTime.MinValue)
                .FirstOrDefault();

            var (state, anchor, owner) = ResolveState(
                assetType, activePublished, latestAsset, latestJob,
                latestAsset is null ? Array.Empty<ReviewAssignment>() : assignmentsByAsset[latestAsset.AssetId]);

            var queueAgeSeconds = anchor is null
                ? (long?)null
                : Math.Max(0, (long)(now - anchor.Value).TotalSeconds);

            result.Add(new CoverageStatus
            {
                LessonId = lessonId,
                AssetType = assetType,
                State = state,
                QueueAge = queueAgeSeconds,
                Owner = owner,
                LastUpdatedAt = anchor ?? now
            });
        }

        return result;
    }

    private static (CoverageState state, DateTime? anchor, string? owner) ResolveState(
        AssetType assetType,
        PublishedAsset? activePublished,
        GeneratedAsset? latestAsset,
        GenerationJob? latestJob,
        IEnumerable<ReviewAssignment> assignments)
    {
        if (activePublished is not null)
        {
            return (CoverageState.Approved, activePublished.ApprovedAt, activePublished.ApprovedByExpert);
        }

        if (latestAsset is not null)
        {
            switch (latestAsset.Status)
            {
                case AssetStatus.Rejected:
                case AssetStatus.AutoFailed:
                case AssetStatus.Invalidated:
                    return (CoverageState.Failed, latestAsset.ProducedAt, latestAsset.ProducedBy);

                case AssetStatus.PendingAdminReview:
                case AssetStatus.PendingExpertReview:
                case AssetStatus.EditRequested:
                    var openAssignment = assignments
                        .Where(a => a.Status == ReviewAssignmentStatus.Open || a.Status == ReviewAssignmentStatus.InReview)
                        .OrderBy(a => a.AssignedAt)
                        .FirstOrDefault();
                    return (
                        CoverageState.PendingReview,
                        openAssignment?.AssignedAt ?? latestAsset.ProducedAt,
                        openAssignment?.AssignedTo ?? latestAsset.ProducedBy);

                case AssetStatus.Queued:
                case AssetStatus.Producing:
                case AssetStatus.AutoValidating:
                    return (CoverageState.InProduction, latestAsset.ProducedAt, latestAsset.ProducedBy);

                case AssetStatus.Approved:
                    // Approved but not yet published — treat as pending until a PublishedAsset is active.
                    return (CoverageState.PendingReview, latestAsset.ProducedAt, latestAsset.ProducedBy);

                case AssetStatus.Superseded:
                    // Superseded means a newer version exists; the projection will see the newer asset.
                    return (CoverageState.NotStarted, null, null);
            }
        }

        if (latestJob is not null)
        {
            switch (latestJob.Status)
            {
                case GenerationJobStatus.Queued:
                case GenerationJobStatus.Running:
                    return (CoverageState.InProduction, latestJob.StartedAt, null);

                case GenerationJobStatus.Failed:
                case GenerationJobStatus.PartialFailed:
                    return (CoverageState.Failed, latestJob.CompletedAt ?? latestJob.StartedAt, null);
            }
        }

        return (CoverageState.NotStarted, null, null);
    }

    /// <summary>
    /// Returns true when a job scope string contains the asset type. Scope is
    /// a JSON array such as <c>["TextSummary","Audio"]</c>; for resilience we
    /// treat an empty or null scope as covering all tracked asset types.
    /// </summary>
    public static bool JobScopeCovers(string? scope, AssetType assetType)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "[]") return true;
        return scope.Contains($"\"{assetType}\"", StringComparison.OrdinalIgnoreCase);
    }
}
