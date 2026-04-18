using Muallimi.Api.Observability.DistributedTracing;
using Muallimi.Domain.AiOperations;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Observability;

/// <summary>
/// T077 — Ensures the trace query service assembles spans from AI request
/// records, Phase 6 metrics, operational events, and audit entries that
/// share a correlation_id.
/// </summary>
public class DistributedTraceQueryServiceTests
{
    [Fact]
    public async Task GetTraceAsync_returns_null_for_unknown_correlation_id()
    {
        var db = Phase6TestDbContextFactory.Create();
        var service = new DistributedTraceQueryService(db);
        var trace = await service.GetTraceAsync("unknown-corr");
        Assert.Null(trace);
    }

    [Fact]
    public async Task GetTraceAsync_assembles_spans_from_multiple_sources()
    {
        var db = Phase6TestDbContextFactory.Create();
        const string corr = "corr-trace-1";
        var t0 = DateTime.UtcNow.AddSeconds(-30);

        db.AiRequestRecords.Add(new AiRequestRecord
        {
            RecordId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            CorrelationId = corr,
            LatencyMs = 250,
            FinalOutcome = "answered",
            SessionMode = "tutor_chat",
            CurriculumType = "moe",
            Grade = "g5",
            Subject = "math",
            TutorLanguage = "ar",
            Stages = "[]",
            RoutingDecision = "{}",
            PromptVersionsUsed = "{}",
            OccurredAt = t0,
        });
        db.AuditEntries.Add(new AuditEntry
        {
            AuditEntryId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            ActorId = Guid.NewGuid(),
            ActorType = "operator",
            ActionType = "incident.created",
            CorrelationId = corr,
            OccurredAt = t0.AddSeconds(1),
        });
        await db.SaveChangesAsync();

        var service = new DistributedTraceQueryService(db);
        var trace = await service.GetTraceAsync(corr);

        Assert.NotNull(trace);
        Assert.Equal(corr, trace!.CorrelationId);
        Assert.Equal(2, trace.Spans.Count);
        Assert.All(trace.Spans.Skip(1), s => Assert.NotNull(s.ParentSpanId));
    }
}
