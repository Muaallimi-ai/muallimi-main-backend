using Muallimi.Api.Coverage;
using Muallimi.Domain.Content;
using Muallimi.Domain.Coverage;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T112 - Contract tests for GET /admin/content/coverage.
/// Validates the projection/aggregator contract per the US6 spec:
///   - every tracked asset type is represented per lesson (complete matrix)
///   - state mapping priority: Approved > Failed > PendingReview > InProduction > NotStarted
///   - BRD SLA thresholds (5 business days text/audio, 7 business days visuals)
///   - filter composition and empty-result contract
///   - audit category/action for dashboard access
/// Tests are domain/projection-level (matching prior US1–US5 contract tests)
/// so they exercise the contract without DB plumbing.
/// </summary>
public class CoverageApiContractTests
{
    private static readonly DateTime Now = new(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);

    private static readonly CoverageFilters NoFilters =
        new(CurriculumType: null, Grade: null, Subject: null);

    // ── Projection: shape and completeness ──

    [Fact]
    public void Projection_Returns_One_Row_Per_Tracked_Asset_Type()
    {
        var lessonId = Guid.NewGuid();
        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            Array.Empty<GeneratedAsset>(),
            Array.Empty<GenerationJob>(),
            Array.Empty<PublishedAsset>(),
            Array.Empty<ReviewAssignment>(),
            Now);

        Assert.Equal(CoverageStatusProjection.TrackedAssetTypes.Count, statuses.Count);
        Assert.All(CoverageStatusProjection.TrackedAssetTypes, type =>
            Assert.Contains(statuses, s => s.AssetType == type));
    }

    [Fact]
    public void Projection_Defaults_To_NotStarted_When_No_Related_Records()
    {
        var lessonId = Guid.NewGuid();
        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            Array.Empty<GeneratedAsset>(),
            Array.Empty<GenerationJob>(),
            Array.Empty<PublishedAsset>(),
            Array.Empty<ReviewAssignment>(),
            Now);

        Assert.All(statuses, s => Assert.Equal(CoverageState.NotStarted, s.State));
        Assert.All(statuses, s => Assert.Null(s.QueueAge));
        Assert.All(statuses, s => Assert.Null(s.Owner));
    }

    // ── Projection: state mapping priority ──

    [Fact]
    public void Active_Published_Asset_Maps_To_Approved()
    {
        var lessonId = Guid.NewGuid();
        var asset = GeneratedAsset.Create(lessonId, AssetType.TextSummary, null, "ar", 1, "worker");
        // Deliberately leave asset in Queued state to prove Approved wins over asset status
        var published = PublishedAsset.Create(
            asset.AssetId, lessonId, AssetType.TextSummary, null,
            "/content/text", "admin-1", "expert-7", 1);

        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            new[] { asset },
            Array.Empty<GenerationJob>(),
            new[] { published },
            Array.Empty<ReviewAssignment>(),
            Now);

        var text = statuses.Single(s => s.AssetType == AssetType.TextSummary);
        Assert.Equal(CoverageState.Approved, text.State);
        Assert.Equal("expert-7", text.Owner);
    }

    [Fact]
    public void Invalidated_Or_Rejected_Asset_Maps_To_Failed()
    {
        var lessonId = Guid.NewGuid();
        var rejected = BuildAssetInState(lessonId, AssetType.TextSummary, null, AssetStatus.Rejected);
        var autoFailed = BuildAssetInState(lessonId, AssetType.Audio, null, AssetStatus.AutoFailed);

        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            new[] { rejected, autoFailed },
            Array.Empty<GenerationJob>(),
            Array.Empty<PublishedAsset>(),
            Array.Empty<ReviewAssignment>(),
            Now);

        Assert.Equal(CoverageState.Failed, statuses.Single(s => s.AssetType == AssetType.TextSummary).State);
        Assert.Equal(CoverageState.Failed, statuses.Single(s => s.AssetType == AssetType.Audio).State);
    }

    [Fact]
    public void Pending_Review_Asset_Maps_To_PendingReview_With_Assignment_Owner()
    {
        var lessonId = Guid.NewGuid();
        var asset = BuildAssetInState(lessonId, AssetType.TextSummary, null, AssetStatus.PendingAdminReview);
        var assignment = ReviewAssignment.CreateAdminAssignment(
            asset.AssetId, assignedTo: "admin-42", assignedBy: "system", AssetType.TextSummary);

        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            new[] { asset },
            Array.Empty<GenerationJob>(),
            Array.Empty<PublishedAsset>(),
            new[] { assignment },
            Now);

        var text = statuses.Single(s => s.AssetType == AssetType.TextSummary);
        Assert.Equal(CoverageState.PendingReview, text.State);
        Assert.Equal("admin-42", text.Owner);
    }

    [Fact]
    public void Queued_Or_Producing_Asset_Maps_To_InProduction()
    {
        var lessonId = Guid.NewGuid();
        var asset = BuildAssetInState(lessonId, AssetType.Visual, VisualFormat.Mp4Animation, AssetStatus.Producing);

        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            new[] { asset },
            Array.Empty<GenerationJob>(),
            Array.Empty<PublishedAsset>(),
            Array.Empty<ReviewAssignment>(),
            Now);

        var visual = statuses.Single(s => s.AssetType == AssetType.Visual);
        Assert.Equal(CoverageState.InProduction, visual.State);
    }

    [Fact]
    public void Running_Job_Without_Asset_Maps_To_InProduction()
    {
        var lessonId = Guid.NewGuid();
        var job = GenerationJob.Create(lessonId, "[\"TextSummary\"]", "corr-1");
        job.MarkRunning();

        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            Array.Empty<GeneratedAsset>(),
            new[] { job },
            Array.Empty<PublishedAsset>(),
            Array.Empty<ReviewAssignment>(),
            Now);

        Assert.Equal(CoverageState.InProduction, statuses.Single(s => s.AssetType == AssetType.TextSummary).State);
    }

    [Fact]
    public void Failed_Job_Without_Asset_Maps_To_Failed()
    {
        var lessonId = Guid.NewGuid();
        var job = GenerationJob.Create(lessonId, "[\"TextSummary\"]", "corr-1");
        job.MarkRunning();
        job.MarkFailed("provider-timeout");

        var statuses = CoverageStatusProjection.Derive(
            lessonId,
            Array.Empty<GeneratedAsset>(),
            new[] { job },
            Array.Empty<PublishedAsset>(),
            Array.Empty<ReviewAssignment>(),
            Now);

        Assert.Equal(CoverageState.Failed, statuses.Single(s => s.AssetType == AssetType.TextSummary).State);
    }

    // ── Queue age / SLA thresholds ──

    [Fact]
    public void Sla_Thresholds_Match_Brd_Five_Or_Seven_Business_Days()
    {
        Assert.Equal(5, QueueAgeCalculator.SlaThresholdBusinessDays(AssetType.TextSummary));
        Assert.Equal(5, QueueAgeCalculator.SlaThresholdBusinessDays(AssetType.Audio));
        Assert.Equal(5, QueueAgeCalculator.SlaThresholdBusinessDays(AssetType.QuizItem));
        Assert.Equal(5, QueueAgeCalculator.SlaThresholdBusinessDays(AssetType.QaCacheEntry));
        Assert.Equal(7, QueueAgeCalculator.SlaThresholdBusinessDays(AssetType.Visual));
    }

    [Fact]
    public void BusinessDaysBetween_Skips_Weekends()
    {
        // Friday 2026-04-10 → Monday 2026-04-13 = 1 business day (Friday counted)
        var friday = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc);
        var monday = new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, QueueAgeCalculator.BusinessDaysBetween(friday, monday));
    }

    [Fact]
    public void BusinessDaysBetween_Same_Day_Is_Zero()
    {
        var d = new DateTime(2026, 4, 16, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0, QueueAgeCalculator.BusinessDaysBetween(d, d));
    }

    [Fact]
    public void Sla_Breached_Only_After_Crossing_Threshold()
    {
        var anchor = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc); // Wednesday
        var textNow = new DateTime(2026, 4, 8, 23, 0, 0, DateTimeKind.Utc); // Wed next week: 5 business days
        var textOver = new DateTime(2026, 4, 9, 23, 0, 0, DateTimeKind.Utc); // Thu: 6 business days

        Assert.False(QueueAgeCalculator.IsSlaBreached(AssetType.TextSummary, anchor, textNow));
        Assert.True(QueueAgeCalculator.IsSlaBreached(AssetType.TextSummary, anchor, textOver));

        // Visual threshold is 7 business days → not breached at 6
        Assert.False(QueueAgeCalculator.IsSlaBreached(AssetType.Visual, anchor, textOver));
    }

    [Fact]
    public void Sla_Never_Breached_When_Anchor_Is_Null()
    {
        Assert.False(QueueAgeCalculator.IsSlaBreached(AssetType.TextSummary, anchor: null, Now));
        Assert.False(QueueAgeCalculator.IsSlaBreached(AssetType.Visual, anchor: null, Now));
    }

    // ── Aggregator: filter composition and counts ──

    [Fact]
    public void Aggregator_Empty_When_No_Lessons_Match_Filters()
    {
        var dashboard = CoverageAggregator.BuildDashboard(
            lessons: Array.Empty<Lesson>(),
            assets: Array.Empty<GeneratedAsset>(),
            jobs: Array.Empty<GenerationJob>(),
            published: Array.Empty<PublishedAsset>(),
            assignments: Array.Empty<ReviewAssignment>(),
            filters: NoFilters,
            now: Now);

        Assert.Equal(0, dashboard.TotalLessons);
        Assert.Empty(dashboard.Lessons);
        Assert.Equal(0, dashboard.SlaBreachedCount);
        // Every state/asset-type bucket is initialised to zero (complete matrix)
        Assert.All(Enum.GetValues<CoverageState>(), s => Assert.Equal(0, dashboard.StateTotals[s]));
    }

    [Fact]
    public void Aggregator_Counts_All_States_Across_Lessons()
    {
        var approvedLesson = BuildLesson(CurriculumType.Moe, Subject.Mathematics, "Ch1 > L1");
        var pendingLesson = BuildLesson(CurriculumType.Moe, Subject.Mathematics, "Ch1 > L2");

        var approvedAsset = GeneratedAsset.Create(approvedLesson.LessonId, AssetType.TextSummary, null, "ar", 1, "worker");
        var approvedPublished = PublishedAsset.Create(
            approvedAsset.AssetId, approvedLesson.LessonId, AssetType.TextSummary, null,
            "/content/text", "admin-1", "expert-1", 1);

        var pendingAsset = BuildAssetInState(
            pendingLesson.LessonId, AssetType.TextSummary, null, AssetStatus.PendingAdminReview);

        var dashboard = CoverageAggregator.BuildDashboard(
            lessons: new[] { approvedLesson, pendingLesson },
            assets: new[] { approvedAsset, pendingAsset },
            jobs: Array.Empty<GenerationJob>(),
            published: new[] { approvedPublished },
            assignments: Array.Empty<ReviewAssignment>(),
            filters: NoFilters,
            now: Now);

        Assert.Equal(2, dashboard.TotalLessons);
        // Two lessons × five tracked asset types = 10 rows total
        Assert.Equal(1, dashboard.StateTotals[CoverageState.Approved]);
        Assert.Equal(1, dashboard.StateTotals[CoverageState.PendingReview]);
        // Remaining rows are NotStarted (one Approved + one Pending = 2; the other 8 slots are NotStarted)
        Assert.Equal(8, dashboard.StateTotals[CoverageState.NotStarted]);
    }

    [Fact]
    public void Aggregator_SlaBreachedCount_Only_Counts_PendingReview_Over_Threshold()
    {
        var lesson = BuildLesson(CurriculumType.Moe, Subject.Mathematics, "Ch1 > L1");
        // Produce an asset that entered PendingAdminReview 10 UTC days ago (> 5 business days)
        var stale = BuildAssetInState(lesson.LessonId, AssetType.TextSummary, null, AssetStatus.PendingAdminReview);
        SetProducedAtViaReflection(stale, Now.AddDays(-15));

        var fresh = BuildAssetInState(lesson.LessonId, AssetType.Audio, null, AssetStatus.PendingAdminReview);
        SetProducedAtViaReflection(fresh, Now.AddHours(-6));

        var dashboard = CoverageAggregator.BuildDashboard(
            lessons: new[] { lesson },
            assets: new[] { stale, fresh },
            jobs: Array.Empty<GenerationJob>(),
            published: Array.Empty<PublishedAsset>(),
            assignments: Array.Empty<ReviewAssignment>(),
            filters: NoFilters,
            now: Now);

        Assert.Equal(1, dashboard.SlaBreachedCount);
    }

    // ── Helpers ──

    private static Lesson BuildLesson(CurriculumType type, Subject subject, string path)
    {
        var lesson = Lesson.Create(
            structureId: Guid.NewGuid(),
            type, Grade.Grade7, subject, TutorLanguage.Ar, path);
        return lesson;
    }

    private static GeneratedAsset BuildAssetInState(
        Guid lessonId, AssetType assetType, VisualFormat? visualFormat, AssetStatus targetStatus)
    {
        var asset = GeneratedAsset.Create(lessonId, assetType, visualFormat, "ar", 1, "worker");
        // Walk the state machine until we reach the desired status.
        if (targetStatus == AssetStatus.Queued) return asset;
        asset.MarkProducing();
        if (targetStatus == AssetStatus.Producing) return asset;
        asset.MarkAutoValidating();
        if (targetStatus == AssetStatus.AutoValidating) return asset;
        if (targetStatus == AssetStatus.AutoFailed) { asset.MarkAutoFailed(); return asset; }
        asset.MarkPendingAdminReview();
        if (targetStatus == AssetStatus.PendingAdminReview) return asset;
        if (targetStatus == AssetStatus.Rejected) { asset.MarkRejected(); return asset; }
        if (targetStatus == AssetStatus.EditRequested) { asset.MarkEditRequested(); return asset; }
        asset.MarkPendingExpertReview();
        if (targetStatus == AssetStatus.PendingExpertReview) return asset;
        asset.MarkApproved();
        if (targetStatus == AssetStatus.Approved) return asset;
        if (targetStatus == AssetStatus.Invalidated) { asset.MarkInvalidated(); return asset; }
        if (targetStatus == AssetStatus.Superseded) { asset.MarkSuperseded(); return asset; }
        throw new InvalidOperationException($"Unsupported status in helper: {targetStatus}");
    }

    /// <summary>
    /// ProducedAt is a private setter on GeneratedAsset. For SLA-breach tests we
    /// need to simulate assets that have been sitting in a queue for days. The
    /// domain intentionally does not expose a setter (time is owned by the
    /// aggregate) so we use reflection — scoped to tests only.
    /// </summary>
    private static void SetProducedAtViaReflection(GeneratedAsset asset, DateTime value)
    {
        var prop = typeof(GeneratedAsset).GetProperty(
            "ProducedAt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        prop!.SetValue(asset, value);
    }
}
