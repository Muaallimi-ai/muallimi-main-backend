using System;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Api.StudentProgressSurface;
using Muallimi.Api.Tests.Integration.Engagement;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T045 (US1) — Tenant isolation integration test for the student progress
/// surface.
///
/// Seeds two tenants (<see cref="TenantIsolationHarness.TenantAlpha"/> and
/// <see cref="TenantIsolationHarness.TenantBeta"/>) with overlapping mastery,
/// streak, and badge rows, then asserts that
/// <see cref="IStudentProgressService.BuildSummaryAsync"/> never returns a
/// row that belongs to the "other" tenant.
///
/// The harness also seeds a focus area owned by tenant beta so the focus-
/// area detail endpoint can be probed with a focusAreaId from beta while
/// authenticated as alpha — the expected outcome is <c>null</c> so existence
/// cannot be leaked across tenants.
/// </summary>
public class StudentProgressTenantIsolationTests
{
    [Fact]
    public async Task Summary_Only_Returns_Rows_For_The_Authenticated_Tenant()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        // Seed mastery + badges + focus area for beta only so alpha's summary
        // must come back empty-in-beta.
        var betaCriterion = new BadgeCriterion
        {
            BadgeCriterionId = Guid.NewGuid(),
            BadgeKey = "consistency_7_day_streak",
            Version = "v1",
            Category = "consistency",
            DisplayNameAr = "مواظب أسبوع",
            DisplayNameEn = "Week-long Streak",
            DescriptionAr = "سبعة أيام متتالية من الدراسة اليومية.",
            DescriptionEn = "Seven consecutive days of daily study.",
            Threshold = "{\"type\":\"streak\",\"days\":7}",
        };
        db.BadgeCriteria.Add(betaCriterion);
        db.BadgeAwards.Add(new BadgeAward
        {
            BadgeAwardId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantBeta,
            StudentId = TenantIsolationHarness.SharedStudentIdBeta,
            BadgeCriterionId = betaCriterion.BadgeCriterionId,
            BadgeCriterionVersion = betaCriterion.Version,
            AwardedAt = DateTime.UtcNow,
            OriginatingProgressRecordIds = "[]",
            CelebrationShown = false,
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
        db.FocusAreas.Add(new FocusArea
        {
            FocusAreaId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantBeta,
            StudentId = TenantIsolationHarness.SharedStudentIdBeta,
            CurriculumType = "moe",
            SubjectId = Guid.NewGuid(),
            ChapterId = Guid.NewGuid(),
            TopicId = Guid.NewGuid(),
            SignalSummary = "{}",
            RationaleAr = "اشتغل على هذا الموضوع",
            RationaleEn = "Practice this topic",
            SuggestedNextStep = "{\"phase3_mode\":\"study\",\"deep_link\":\"/study\"}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(7),
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
        await db.SaveChangesAsync();

        var service = new StudentProgressService(db, new DefaultCurriculumLabelResolver());

        var alphaSummary = await service.BuildSummaryAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedStudentIdAlpha);

        Assert.Equal(TenantIsolationHarness.SharedStudentIdAlpha, alphaSummary.StudentId);
        Assert.Empty(alphaSummary.Badges);
        Assert.Empty(alphaSummary.FocusAreas);
        Assert.Equal(1, alphaSummary.MasteryBySubject.Count); // only the alpha mastery row seeded by the harness
        Assert.Equal(1, alphaSummary.Streak.CurrentLength);

        var betaSummary = await service.BuildSummaryAsync(
            TenantIsolationHarness.TenantBeta,
            TenantIsolationHarness.SharedStudentIdBeta);
        Assert.Single(betaSummary.Badges);
        Assert.Single(betaSummary.FocusAreas);
    }

    [Fact]
    public async Task FocusArea_Detail_Cross_Tenant_Lookup_Returns_Null()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var betaFocusAreaId = Guid.NewGuid();
        db.FocusAreas.Add(new FocusArea
        {
            FocusAreaId = betaFocusAreaId,
            TenantId = TenantIsolationHarness.TenantBeta,
            StudentId = TenantIsolationHarness.SharedStudentIdBeta,
            CurriculumType = "moe",
            SubjectId = Guid.NewGuid(),
            ChapterId = Guid.NewGuid(),
            TopicId = Guid.NewGuid(),
            SignalSummary = "{}",
            RationaleAr = "تحسين",
            RationaleEn = "Improve",
            SuggestedNextStep = "{\"phase3_mode\":\"solve_questions\",\"deep_link\":\"/solve-questions\"}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(1),
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
        await db.SaveChangesAsync();

        var service = new StudentProgressService(db, new DefaultCurriculumLabelResolver());

        var crossTenant = await service.GetFocusAreaDetailAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedStudentIdAlpha,
            betaFocusAreaId);
        Assert.Null(crossTenant);

        var sameTenant = await service.GetFocusAreaDetailAsync(
            TenantIsolationHarness.TenantBeta,
            TenantIsolationHarness.SharedStudentIdBeta,
            betaFocusAreaId);
        Assert.NotNull(sameTenant);
    }

    [Fact]
    public async Task Celebration_Shown_Endpoint_Is_Idempotent_And_TenantScoped()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var awardId = Guid.NewGuid();
        db.BadgeAwards.Add(new BadgeAward
        {
            BadgeAwardId = awardId,
            TenantId = TenantIsolationHarness.TenantAlpha,
            StudentId = TenantIsolationHarness.SharedStudentIdAlpha,
            BadgeCriterionId = Guid.NewGuid(),
            BadgeCriterionVersion = "v1",
            AwardedAt = DateTime.UtcNow,
            OriginatingProgressRecordIds = "[]",
            CelebrationShown = false,
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
        await db.SaveChangesAsync();

        var service = new StudentProgressService(db, new DefaultCurriculumLabelResolver());

        var crossTenant = await service.MarkBadgeCelebrationShownAsync(
            TenantIsolationHarness.TenantBeta,
            TenantIsolationHarness.SharedStudentIdBeta,
            awardId);
        Assert.Equal(BadgeCelebrationOutcome.NotFound, crossTenant);

        var first = await service.MarkBadgeCelebrationShownAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedStudentIdAlpha,
            awardId);
        Assert.Equal(BadgeCelebrationOutcome.Marked, first);

        var second = await service.MarkBadgeCelebrationShownAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedStudentIdAlpha,
            awardId);
        Assert.Equal(BadgeCelebrationOutcome.AlreadyShown, second);
    }
}
