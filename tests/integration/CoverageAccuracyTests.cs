using Muallimi.Api.Coverage;
using Muallimi.Domain.Content;
using Muallimi.Domain.Coverage;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T113 - Integration tests asserting dashboard accuracy against a seeded
/// mixed-state world. Per the US6 independent test:
///
///   "Seed a mixed state across lessons and asset types and verify the
///    dashboard reports accurate counts per state, correct filters by
///    curriculum type/grade/subject, and highlights SLA-aged queue items."
///
/// These tests drive the in-memory aggregator (not EF) so they run on any
/// environment. The EF projection is exercised indirectly by the same code
/// paths.
/// </summary>
public class CoverageAccuracyTests
{
    private static readonly DateTime Now = new(2026, 4, 16, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Mixed_State_Counts_Match_Seed()
    {
        var seed = BuildMixedSeed();

        var dashboard = CoverageAggregator.BuildDashboard(
            seed.Lessons, seed.Assets, seed.Jobs, seed.Published, seed.Assignments,
            new CoverageFilters(null, null, null), Now);

        Assert.Equal(3, dashboard.TotalLessons);

        // Lesson 1: TextSummary Approved, Audio PendingReview, Visual Failed → 3 populated
        // Lesson 2: TextSummary InProduction (job running), rest NotStarted
        // Lesson 3: TextSummary PendingReview (SLA-breached), rest NotStarted
        Assert.Equal(1, dashboard.StateTotals[CoverageState.Approved]);
        Assert.Equal(2, dashboard.StateTotals[CoverageState.PendingReview]);
        Assert.Equal(1, dashboard.StateTotals[CoverageState.Failed]);
        Assert.Equal(1, dashboard.StateTotals[CoverageState.InProduction]);

        // Remaining rows in the 3×5 matrix default to NotStarted
        var totalRows = dashboard.StateTotals.Values.Sum();
        Assert.Equal(3 * CoverageStatusProjection.TrackedAssetTypes.Count, totalRows);
        Assert.Equal(
            totalRows - (1 + 2 + 1 + 1),
            dashboard.StateTotals[CoverageState.NotStarted]);

        // One SLA-breached pending item is highlighted
        Assert.Equal(1, dashboard.SlaBreachedCount);
    }

    [Fact]
    public void Per_Asset_Type_Counts_Break_Down_Correctly()
    {
        var seed = BuildMixedSeed();

        var dashboard = CoverageAggregator.BuildDashboard(
            seed.Lessons, seed.Assets, seed.Jobs, seed.Published, seed.Assignments,
            new CoverageFilters(null, null, null), Now);

        var textSummary = dashboard.AssetTypeTotals[AssetType.TextSummary];
        Assert.Equal(1, textSummary[CoverageState.Approved]);           // Lesson 1
        Assert.Equal(1, textSummary[CoverageState.InProduction]);       // Lesson 2
        Assert.Equal(1, textSummary[CoverageState.PendingReview]);      // Lesson 3

        var audio = dashboard.AssetTypeTotals[AssetType.Audio];
        Assert.Equal(1, audio[CoverageState.PendingReview]);
        Assert.Equal(2, audio[CoverageState.NotStarted]);

        var visual = dashboard.AssetTypeTotals[AssetType.Visual];
        Assert.Equal(1, visual[CoverageState.Failed]);
        Assert.Equal(2, visual[CoverageState.NotStarted]);
    }

    [Fact]
    public void Filter_By_CurriculumType_Excludes_Other_Types()
    {
        var seed = BuildMixedSeed();

        // Only Lessons 1 and 2 are MOE
        var moeLessons = seed.Lessons.Where(l => l.CurriculumType == CurriculumType.Moe).ToList();

        var dashboard = CoverageAggregator.BuildDashboard(
            moeLessons, seed.Assets, seed.Jobs, seed.Published, seed.Assignments,
            new CoverageFilters(CurriculumType.Moe, null, null), Now);

        Assert.Equal(2, dashboard.TotalLessons);
        Assert.DoesNotContain(dashboard.Lessons, l => l.CurriculumType == CurriculumType.International);
        // SLA breach was on the International lesson → filtered out
        Assert.Equal(0, dashboard.SlaBreachedCount);
    }

    [Fact]
    public void Filter_By_Subject_Narrows_To_Matching_Lessons_Only()
    {
        var seed = BuildMixedSeed();

        var scienceLessons = seed.Lessons.Where(l => l.Subject == Subject.Science).ToList();
        var dashboard = CoverageAggregator.BuildDashboard(
            scienceLessons, seed.Assets, seed.Jobs, seed.Published, seed.Assignments,
            new CoverageFilters(null, null, Subject.Science), Now);

        // Only Lesson 3 is Science
        Assert.Equal(1, dashboard.TotalLessons);
        Assert.Single(dashboard.Lessons, l => l.Subject == Subject.Science);
    }

    [Fact]
    public void Combined_Filters_Apply_All_Constraints()
    {
        var seed = BuildMixedSeed();

        // MOE + Grade7 + Mathematics matches lessons 1 and 2
        var filtered = seed.Lessons
            .Where(l => l.CurriculumType == CurriculumType.Moe
                && l.Grade == Grade.Grade7
                && l.Subject == Subject.Mathematics)
            .ToList();

        var dashboard = CoverageAggregator.BuildDashboard(
            filtered, seed.Assets, seed.Jobs, seed.Published, seed.Assignments,
            new CoverageFilters(CurriculumType.Moe, Grade.Grade7, Subject.Mathematics), Now);

        Assert.Equal(2, dashboard.TotalLessons);
        Assert.All(dashboard.Lessons, l =>
        {
            Assert.Equal(CurriculumType.Moe, l.CurriculumType);
            Assert.Equal(Grade.Grade7, l.Grade);
            Assert.Equal(Subject.Mathematics, l.Subject);
        });
    }

    [Fact]
    public void SLA_Aged_Items_Are_Surfaced_Only_For_Pending_Review()
    {
        var seed = BuildMixedSeed();
        var dashboard = CoverageAggregator.BuildDashboard(
            seed.Lessons, seed.Assets, seed.Jobs, seed.Published, seed.Assignments,
            new CoverageFilters(null, null, null), Now);

        var slaRows = dashboard.Lessons
            .SelectMany(l => l.Assets.Select(a => (lesson: l, asset: a)))
            .Where(x => x.asset.SlaBreached)
            .ToList();

        Assert.Single(slaRows);
        // Only a PendingReview row can breach SLA (Failed/Approved/NotStarted never flag)
        Assert.Equal(CoverageState.PendingReview, slaRows[0].asset.State);
        // Text threshold is 5 business days; the stale row should report ≥ 6
        Assert.True(slaRows[0].asset.QueueAgeBusinessDays > 5,
            $"Expected business-day age > 5 but got {slaRows[0].asset.QueueAgeBusinessDays}");
        Assert.Equal(5, slaRows[0].asset.SlaThresholdBusinessDays);
    }

    [Fact]
    public void Approved_And_Failed_Rows_Never_Flag_SlaBreached()
    {
        var seed = BuildMixedSeed();
        var dashboard = CoverageAggregator.BuildDashboard(
            seed.Lessons, seed.Assets, seed.Jobs, seed.Published, seed.Assignments,
            new CoverageFilters(null, null, null), Now);

        var nonPending = dashboard.Lessons
            .SelectMany(l => l.Assets)
            .Where(a => a.State != CoverageState.PendingReview);

        Assert.All(nonPending, a => Assert.False(a.SlaBreached));
    }

    // ── Seed construction ──

    private sealed record Seed(
        IReadOnlyList<Lesson> Lessons,
        IReadOnlyList<GeneratedAsset> Assets,
        IReadOnlyList<GenerationJob> Jobs,
        IReadOnlyList<PublishedAsset> Published,
        IReadOnlyList<ReviewAssignment> Assignments);

    private static Seed BuildMixedSeed()
    {
        var structureId = Guid.NewGuid();

        var lesson1 = Lesson.Create(structureId, CurriculumType.Moe, Grade.Grade7,
            Subject.Mathematics, TutorLanguage.Ar, "Ch1 > Lesson 1");
        var lesson2 = Lesson.Create(structureId, CurriculumType.Moe, Grade.Grade7,
            Subject.Mathematics, TutorLanguage.Ar, "Ch1 > Lesson 2");
        var lesson3 = Lesson.Create(structureId, CurriculumType.International, Grade.Grade7,
            Subject.Science, TutorLanguage.Ar, "Ch2 > Lesson 1");

        // ── Lesson 1: TextSummary Approved + Audio PendingReview + Visual Failed ──
        var l1Text = BuildAssetInState(lesson1.LessonId, AssetType.TextSummary, null, AssetStatus.Approved);
        var l1Published = PublishedAsset.Create(
            l1Text.AssetId, lesson1.LessonId, AssetType.TextSummary, null,
            "/content/l1/text", "admin-1", "expert-1", 1);

        var l1Audio = BuildAssetInState(lesson1.LessonId, AssetType.Audio, null, AssetStatus.PendingAdminReview);
        var l1AudioAssignment = ReviewAssignment.CreateAdminAssignment(
            l1Audio.AssetId, "admin-1", "system", AssetType.Audio);

        var l1Visual = BuildAssetInState(
            lesson1.LessonId, AssetType.Visual, VisualFormat.Mp4Animation, AssetStatus.Rejected);

        // ── Lesson 2: TextSummary InProduction via running job ──
        var l2Job = GenerationJob.Create(lesson2.LessonId, "[\"TextSummary\"]", "corr-l2");
        l2Job.MarkRunning();

        // ── Lesson 3: TextSummary PendingReview — stale (SLA-breached) ──
        var l3Text = BuildAssetInState(lesson3.LessonId, AssetType.TextSummary, null, AssetStatus.PendingAdminReview);
        SetProducedAtViaReflection(l3Text, Now.AddDays(-14));
        var l3Assignment = ReviewAssignment.CreateAdminAssignment(
            l3Text.AssetId, "admin-stale", "system", AssetType.TextSummary);
        SetAssignedAtViaReflection(l3Assignment, Now.AddDays(-14));

        return new Seed(
            Lessons: new[] { lesson1, lesson2, lesson3 },
            Assets: new[] { l1Text, l1Audio, l1Visual, l3Text },
            Jobs: new[] { l2Job },
            Published: new[] { l1Published },
            Assignments: new[] { l1AudioAssignment, l3Assignment });
    }

    private static GeneratedAsset BuildAssetInState(
        Guid lessonId, AssetType assetType, VisualFormat? visualFormat, AssetStatus targetStatus)
    {
        var asset = GeneratedAsset.Create(lessonId, assetType, visualFormat, "ar", 1, "worker");
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
        throw new InvalidOperationException($"Unsupported status in helper: {targetStatus}");
    }

    private static void SetProducedAtViaReflection(GeneratedAsset asset, DateTime value)
    {
        var prop = typeof(GeneratedAsset).GetProperty(
            "ProducedAt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        prop!.SetValue(asset, value);
    }

    private static void SetAssignedAtViaReflection(ReviewAssignment assignment, DateTime value)
    {
        var prop = typeof(ReviewAssignment).GetProperty(
            "AssignedAt",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        prop!.SetValue(assignment, value);
    }
}
