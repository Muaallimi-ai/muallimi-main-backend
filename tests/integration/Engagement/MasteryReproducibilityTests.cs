using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Api.Engagement.StreakCalculation;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T032 (US4) — Mastery reproducibility.
///
/// A fresh recompute from the stored <c>ProgressRecord</c> set MUST produce
/// the same <c>mastery_score</c>, <c>mastery_band</c>, and
/// <c>contributing_record_count</c> as the incremental pipeline. This is the
/// contract that lets Phase 4 investigate parent-facing mastery regressions
/// from database state alone.
///
/// The test ingests a mixed event stream through the incremental pipeline
/// and then re-runs <see cref="MasteryCalculator.RecomputeAsync"/> against a
/// fresh DbContext with no prior <c>MasteryState</c> row, asserting the
/// result matches the incremental row's fields.
/// </summary>
public class MasteryReproducibilityTests
{
    [Fact]
    public async Task Recompute_From_Stored_ProgressRecord_Set_Matches_Incremental_Pipeline()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        var baseDay = new DateTime(2026, 02, 10, 09, 00, 00, DateTimeKind.Utc);
        var stream = new[]
        {
            Phase4PipelineHarness.BuildEnvelope("evt-1", tenantId, studentId, "lesson_view",        baseDay.AddMinutes(1), subjectId, topicId, new { }),
            Phase4PipelineHarness.BuildEnvelope("evt-2", tenantId, studentId, "content_play",       baseDay.AddMinutes(2), subjectId, topicId, new { }),
            Phase4PipelineHarness.BuildEnvelope("evt-3", tenantId, studentId, "quiz_answered",      baseDay.AddMinutes(3), subjectId, topicId, new { is_correct = true }),
            Phase4PipelineHarness.BuildEnvelope("evt-4", tenantId, studentId, "quiz_answered",      baseDay.AddMinutes(4), subjectId, topicId, new { is_correct = true }),
            Phase4PipelineHarness.BuildEnvelope("evt-5", tenantId, studentId, "quiz_answered",      baseDay.AddMinutes(5), subjectId, topicId, new { is_correct = false }),
            Phase4PipelineHarness.BuildEnvelope("evt-6", tenantId, studentId, "homework_help_used", baseDay.AddMinutes(6), subjectId, topicId, new { }),
            Phase4PipelineHarness.BuildEnvelope("evt-7", tenantId, studentId, "whiteboard_session", baseDay.AddMinutes(7), subjectId, topicId, new { plan_tier_snapshot = "standard" }),
            Phase4PipelineHarness.BuildEnvelope("evt-8", tenantId, studentId, "mock_test",          baseDay.AddMinutes(8), subjectId, topicId, new { correct_count = 3 }),
        };
        foreach (var e in stream)
        {
            await harness.IngestAsync(e);
        }

        await using var db = harness.NewDb();
        var incremental = await db.MasteryStates.IgnoreQueryFilters()
            .SingleAsync(m => m.TenantId == tenantId && m.StudentId == studentId && m.SubjectId == subjectId && m.TopicId == topicId);

        // Reproducibility: build a calculator against a sibling DbContext that
        // has the same PR rows but no MasteryState, and observe the recomputed
        // result.
        await using var sibling = harness.NewDb();
        // Remove the incremental mastery row so the recompute inserts a new one.
        sibling.MasteryStates.RemoveRange(sibling.MasteryStates.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.StudentId == studentId));
        await sibling.SaveChangesAsync();

        var calc = new MasteryCalculator(new ProgressRecordRepository(sibling), new MasteryStateRepository(sibling));
        var result = await calc.RecomputeAsync(
            tenantId, studentId, subjectId, topicId,
            curriculumType: "moe",
            correlationId: Guid.NewGuid().ToString("D"));
        await sibling.SaveChangesAsync();

        Assert.Equal(incremental.MasteryScore, result.NewScore);
        Assert.Equal(incremental.MasteryBand, result.NewBand);
        Assert.Equal(incremental.ContributingRecordCount, stream.Length);
        Assert.Equal(stream.Length, result.State.ContributingRecordCount);
    }
}
