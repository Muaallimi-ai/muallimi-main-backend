using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.ProgressIngestion;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T031 (US4) — Dead-letter behaviour for permanently rejected events.
///
/// Permanent rejections produce a row in
/// <c>progress_ingestion_dead_letters</c> and return
/// <see cref="ProgressIngestionOutcome.Rejected"/>. Rejected events MUST NOT
/// create any <c>ProgressRecord</c>, <c>MasteryState</c>, <c>StreakState</c>,
/// or outbox row — they roll back cleanly.
///
/// Transient failures (broker/DB outage) are NOT written to the dead-letter
/// store; they stay on the broker queue for retry. Those scenarios are
/// exercised by <c>Phase4DownstreamEventDispatcher</c>-level tests in the
/// polish phase.
/// </summary>
public class ProgressIngestionDeadLetterTests
{
    [Fact]
    public async Task Unknown_Event_Kind_Is_Dead_Lettered_With_Reason()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        var envelope = Phase4PipelineHarness.BuildEnvelope(
            "evt-bad-1", tenantId, studentId, "invented_kind",
            DateTime.UtcNow, Guid.NewGuid(), Guid.NewGuid(), new { });

        var outcome = await harness.IngestAsync(envelope);
        Assert.Equal(ProgressIngestionOutcome.Rejected, outcome);

        await using var db = harness.NewDb();
        Assert.Equal(0, await db.ProgressRecords.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.MasteryStates.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.StreakStates.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Phase4DownstreamEvents.IgnoreQueryFilters().CountAsync());

        var dead = await db.ProgressIngestionDeadLetters.SingleAsync();
        Assert.Equal(ProgressIngestionDeadLetterReasons.UnknownEventKind, dead.Reason);
        Assert.Equal("evt-bad-1", dead.SourceEventId);
        Assert.Equal(tenantId, dead.TenantId);
    }

    [Fact]
    public async Task Empty_Tenant_Guid_Is_Dead_Lettered_As_Tenant_Not_Found()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var envelope = Phase4PipelineHarness.BuildEnvelope(
            "evt-no-tenant", tenantId: Guid.Empty, studentId: Guid.NewGuid(),
            kind: "session_start", occurredAtUtc: DateTime.UtcNow);

        var outcome = await harness.IngestAsync(envelope);
        Assert.Equal(ProgressIngestionOutcome.Rejected, outcome);

        await using var db = harness.NewDb();
        var dead = await db.ProgressIngestionDeadLetters.SingleAsync();
        Assert.Equal(ProgressIngestionDeadLetterReasons.TenantNotFound, dead.Reason);
    }

    [Fact]
    public async Task Empty_Student_Guid_Is_Dead_Lettered_As_Student_Not_Found()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var envelope = Phase4PipelineHarness.BuildEnvelope(
            "evt-no-student", tenantId: Guid.NewGuid(), studentId: Guid.Empty,
            kind: "session_start", occurredAtUtc: DateTime.UtcNow);

        var outcome = await harness.IngestAsync(envelope);
        Assert.Equal(ProgressIngestionOutcome.Rejected, outcome);

        await using var db = harness.NewDb();
        var dead = await db.ProgressIngestionDeadLetters.SingleAsync();
        Assert.Equal(ProgressIngestionDeadLetterReasons.StudentNotFound, dead.Reason);
    }

    [Fact]
    public async Task Missing_Source_Event_Id_Is_Dead_Lettered_As_Malformed_Payload()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var envelope = Phase4PipelineHarness.BuildEnvelope(
            sourceEventId: "", tenantId: Guid.NewGuid(), studentId: Guid.NewGuid(),
            kind: "session_start", occurredAtUtc: DateTime.UtcNow);

        var outcome = await harness.IngestAsync(envelope);
        Assert.Equal(ProgressIngestionOutcome.Rejected, outcome);

        await using var db = harness.NewDb();
        var dead = await db.ProgressIngestionDeadLetters.SingleAsync();
        Assert.Equal(ProgressIngestionDeadLetterReasons.MalformedPayload, dead.Reason);
    }
}
