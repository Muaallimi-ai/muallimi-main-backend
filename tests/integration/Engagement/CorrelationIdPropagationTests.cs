using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T158 (Polish) — Correlation ID propagates from a Phase 3 session event
/// through ProgressRecord, MasteryState, StreakState, BadgeAward, and on to
/// every row written to the <c>Phase4DownstreamEvent</c> outbox.
///
/// The contract in
/// <c>specs/006-engagement-progress-parent/contracts/phase4-downstream-events-contract.md</c>
/// requires every downstream event to carry the originating correlation
/// identifier — tracing across Phase 3 → main-backend → downstream
/// consumers MUST be end-to-end.
/// </summary>
public class CorrelationIdPropagationTests
{
    [Fact]
    public async Task Ingestion_Correlation_Id_Appears_On_Every_DownstreamEvent()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        var correlationId = "trace-from-phase3-0042";
        var env = Phase4PipelineHarness.BuildEnvelope(
            "evt-corr-1", tenantId, studentId, "quiz_answered",
            new DateTime(2026, 03, 10, 09, 30, 00, DateTimeKind.Utc),
            subjectId, topicId,
            payload: new { question_id = "q1", is_correct = true, score = 1 },
            correlationId: correlationId);

        await harness.IngestAsync(env);

        await using var db = harness.NewDb();
        var progressRows = await db.ProgressRecords.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.StudentId == studentId).ToListAsync();
        Assert.All(progressRows, r => Assert.Equal(correlationId, r.CorrelationId));

        var masteryRows = await db.MasteryStates.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.StudentId == studentId).ToListAsync();
        Assert.All(masteryRows, r => Assert.Equal(correlationId, r.LastCorrelationId));

        var dsRows = await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.StudentId == studentId).ToListAsync();
        Assert.NotEmpty(dsRows);
        Assert.All(dsRows, e => Assert.Equal(correlationId, e.CorrelationId));
    }

    [Fact]
    public async Task Missing_Correlation_Id_On_Envelope_Is_Backfilled_But_Still_Propagated_Downstream()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        var env = Phase4PipelineHarness.BuildEnvelope(
            "evt-corr-missing", tenantId, studentId, "lesson_view",
            new DateTime(2026, 03, 11, 09, 30, 00, DateTimeKind.Utc),
            subjectId, topicId,
            correlationId: "   "); // whitespace => backfill path in ProgressIngestionWorker

        await harness.IngestAsync(env);

        await using var db = harness.NewDb();
        var progress = await db.ProgressRecords.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId).SingleAsync();
        Assert.False(string.IsNullOrWhiteSpace(progress.CorrelationId));

        var dsRows = await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId).ToListAsync();
        Assert.NotEmpty(dsRows);
        // Every downstream row must carry the SAME backfilled correlation
        // identifier that was chosen at ingestion time — not a per-row value.
        var expected = progress.CorrelationId;
        Assert.All(dsRows, e => Assert.Equal(expected, e.CorrelationId));
    }
}
