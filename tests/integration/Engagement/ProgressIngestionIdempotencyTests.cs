using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T029 (US4) — Idempotent replay under
/// UNIQUE (tenant_id, source_event_id).
///
/// Re-ingesting the identical envelope MUST:
/// 1. Return <see cref="ProgressIngestionOutcome.Duplicate"/> on the second and
///    third call.
/// 2. Leave exactly one <c>ProgressRecord</c> row.
/// 3. Leave exactly one <c>MasteryState</c> row with
///    <c>contributing_record_count = 1</c>.
/// 4. Leave exactly one <c>StreakState</c> with <c>current_length = 1</c>.
/// 5. NOT duplicate any <c>Phase4DownstreamEvent</c> outbox rows.
/// </summary>
public class ProgressIngestionIdempotencyTests
{
    [Fact]
    public async Task Replay_Of_Same_Source_Event_Id_Is_A_NoOp_After_First_Ingest()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        var envelope = Phase4PipelineHarness.BuildEnvelope(
            sourceEventId: "evt-quiz-001",
            tenantId: tenantId,
            studentId: studentId,
            kind: "quiz_answered",
            occurredAtUtc: DateTime.UtcNow,
            subjectId: subjectId,
            topicId: topicId,
            payload: new { is_correct = true });

        var first = await harness.IngestAsync(envelope);
        var second = await harness.IngestAsync(envelope);
        var third = await harness.IngestAsync(envelope);

        Assert.Equal(ProgressIngestionOutcome.Inserted, first);
        Assert.Equal(ProgressIngestionOutcome.Duplicate, second);
        Assert.Equal(ProgressIngestionOutcome.Duplicate, third);

        await using var db = harness.NewDb();
        var prCount = await db.ProgressRecords.IgnoreQueryFilters()
            .CountAsync(p => p.TenantId == tenantId && p.SourceEventId == "evt-quiz-001");
        Assert.Equal(1, prCount);

        var masteryRows = await db.MasteryStates.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.StudentId == studentId)
            .ToListAsync();
        Assert.Single(masteryRows);
        Assert.Equal(1, masteryRows[0].ContributingRecordCount);

        var streakRows = await db.StreakStates.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.StudentId == studentId)
            .ToListAsync();
        Assert.Single(streakRows);
        Assert.Equal(1, streakRows[0].CurrentLength);

        // Each ingestion emitted at most one mastery_updated and at most one
        // streak_changed for this single-event student. Replays MUST NOT add
        // more outbox rows.
        var masteryEvents = await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.EventKind == "mastery_updated")
            .CountAsync();
        var streakEvents = await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.EventKind == "streak_changed")
            .CountAsync();
        Assert.Equal(1, masteryEvents);
        Assert.Equal(1, streakEvents);
    }
}
