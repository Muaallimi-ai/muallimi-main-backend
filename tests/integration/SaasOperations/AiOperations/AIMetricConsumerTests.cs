using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.AiOperations.MetricAggregation;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.AiOperations;

public class AIMetricConsumerTests
{
    private static AIMetricIngestionEvent MakeEvent(
        string phase = "phase2_tutor",
        string promptKey = "tutor.answer",
        string outcome = "pass",
        decimal cost = 0.42m,
        int latency = 120,
        bool refusal = false,
        DateTime? occurredAt = null,
        Guid? tenant = null) => new(
            MetricId: Guid.NewGuid().ToString("N"),
            TenantId: (tenant ?? Guid.NewGuid()).ToString("D"),
            Phase: phase,
            PromptKey: promptKey,
            PromptVersion: "v1",
            ProviderName: "anthropic-lightweight",
            InputTokens: 200,
            OutputTokens: 400,
            EstimatedCostEgp: cost,
            LatencyMs: latency,
            GuardrailOutcome: outcome,
            ConfidenceScore: 0.9m,
            WasRefusal: refusal,
            CorrelationId: "corr-1",
            OccurredAt: occurredAt ?? DateTime.UtcNow);

    [Fact]
    public async Task IngestAsync_writes_metric_and_upserts_three_aggregate_periods()
    {
        var db = Phase6TestDbContextFactory.Create();
        var consumer = new AIMetricConsumer(db, NullLogger<AIMetricConsumer>.Instance);

        await consumer.IngestAsync(MakeEvent());

        Assert.Single(db.Phase6AIOperationsMetrics);
        var agg = await db.AIOperationsAggregates.ToListAsync();
        Assert.Equal(3, agg.Count);
        Assert.Contains(agg, a => a.PeriodType == "hourly");
        Assert.Contains(agg, a => a.PeriodType == "daily");
        Assert.Contains(agg, a => a.PeriodType == "weekly");
    }

    [Fact]
    public async Task IngestAsync_accumulates_across_events_in_same_bucket()
    {
        var db = Phase6TestDbContextFactory.Create();
        var consumer = new AIMetricConsumer(db, NullLogger<AIMetricConsumer>.Instance);
        var tenant = Guid.NewGuid();
        var t0 = new DateTime(2026, 4, 18, 10, 5, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 4, 18, 10, 55, 0, DateTimeKind.Utc);

        await consumer.IngestAsync(MakeEvent(cost: 1m, latency: 100, tenant: tenant, occurredAt: t0));
        await consumer.IngestAsync(MakeEvent(cost: 2m, latency: 300, outcome: "block", refusal: true, tenant: tenant, occurredAt: t1));

        var hourly = await db.AIOperationsAggregates.SingleAsync(a => a.PeriodType == "hourly");
        Assert.Equal(2, hourly.RequestCount);
        Assert.Equal(3m, hourly.TotalCostEgp);
        Assert.Equal(1, hourly.GuardrailPassCount);
        Assert.Equal(1, hourly.GuardrailBlockCount);
        Assert.Equal(1, hourly.RefusalCount);
        Assert.Equal(300, hourly.P95LatencyMs);
    }
}
