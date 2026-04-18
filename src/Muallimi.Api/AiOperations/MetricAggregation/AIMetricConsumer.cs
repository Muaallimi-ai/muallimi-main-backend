using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.AiOperations.MetricAggregation;

/// <summary>
/// T067 (US3) — Consumes <c>ai.operations.metrics</c> events from ai-service.
/// Writes the raw <see cref="AIOperationsMetric"/> row, then updates the
/// pre-aggregated <see cref="AIOperationsAggregate"/> views for hourly, daily,
/// and weekly buckets (each scoped by tenant × phase × prompt_key).
///
/// Local parity: ai-service posts to POST /internal/ai-ops/metrics which calls
/// <see cref="IngestAsync"/>. Production swaps the entry point for a broker
/// subscription without changing the aggregation math.
/// </summary>
public interface IAIMetricConsumer
{
    Task IngestAsync(AIMetricIngestionEvent evt, CancellationToken ct = default);
    Task<int> IngestBatchAsync(IEnumerable<AIMetricIngestionEvent> events, CancellationToken ct = default);
}

public sealed record AIMetricIngestionEvent(
    string MetricId,
    string TenantId,
    string Phase,
    string PromptKey,
    string PromptVersion,
    string ProviderName,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCostEgp,
    int LatencyMs,
    string GuardrailOutcome,
    decimal? ConfidenceScore,
    bool WasRefusal,
    string CorrelationId,
    DateTime OccurredAt);

public sealed class AIMetricConsumer : IAIMetricConsumer
{
    private static readonly string[] PeriodTypes = { "hourly", "daily", "weekly" };

    private readonly MuallimiDbContext _db;
    private readonly ILogger<AIMetricConsumer> _logger;

    public AIMetricConsumer(MuallimiDbContext db, ILogger<AIMetricConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task IngestAsync(AIMetricIngestionEvent evt, CancellationToken ct = default)
    {
        var tenantGuid = TryParseTenant(evt.TenantId);
        var metric = new AIOperationsMetric
        {
            MetricId = TryParseGuid(evt.MetricId) ?? Guid.NewGuid(),
            TenantId = tenantGuid,
            Phase = evt.Phase,
            PromptKey = evt.PromptKey,
            PromptVersion = evt.PromptVersion,
            ProviderName = evt.ProviderName,
            RequestCount = 1,
            TotalInputTokens = evt.InputTokens,
            TotalOutputTokens = evt.OutputTokens,
            EstimatedCostEgp = evt.EstimatedCostEgp,
            LatencyMs = evt.LatencyMs,
            GuardrailOutcome = evt.GuardrailOutcome,
            ConfidenceScore = evt.ConfidenceScore,
            WasRefusal = evt.WasRefusal,
            CorrelationId = evt.CorrelationId,
            OccurredAt = evt.OccurredAt,
        };
        _db.Phase6AIOperationsMetrics.Add(metric);

        foreach (var period in PeriodTypes)
        {
            await UpsertAggregateAsync(period, metric, ct);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<int> IngestBatchAsync(IEnumerable<AIMetricIngestionEvent> events, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var e in events)
        {
            await IngestAsync(e, ct);
            count++;
        }
        return count;
    }

    private async Task UpsertAggregateAsync(string periodType, AIOperationsMetric metric, CancellationToken ct)
    {
        var periodStart = TruncateTo(periodType, metric.OccurredAt);
        var existing = await _db.AIOperationsAggregates
            .Where(a => a.TenantId == metric.TenantId
                     && a.Phase == metric.Phase
                     && a.PromptKey == metric.PromptKey
                     && a.PeriodType == periodType
                     && a.PeriodStart == periodStart)
            .FirstOrDefaultAsync(ct);

        if (existing is null)
        {
            existing = new AIOperationsAggregate
            {
                AggregateId = Guid.NewGuid(),
                TenantId = metric.TenantId,
                Phase = metric.Phase,
                PromptKey = metric.PromptKey,
                PeriodType = periodType,
                PeriodStart = periodStart,
                RequestCount = 0,
                TotalInputTokens = 0,
                TotalOutputTokens = 0,
                TotalCostEgp = 0m,
                AvgLatencyMs = 0,
                P95LatencyMs = 0,
                P99LatencyMs = 0,
                GuardrailPassCount = 0,
                GuardrailWarnCount = 0,
                GuardrailBlockCount = 0,
                RefusalCount = 0,
                ComputedAt = DateTime.UtcNow,
            };
            _db.AIOperationsAggregates.Add(existing);
        }

        var newCount = existing.RequestCount + 1;
        existing.AvgLatencyMs = ((existing.AvgLatencyMs * existing.RequestCount) + metric.LatencyMs) / newCount;
        if (metric.LatencyMs > existing.P95LatencyMs) existing.P95LatencyMs = metric.LatencyMs;
        if (metric.LatencyMs > existing.P99LatencyMs) existing.P99LatencyMs = metric.LatencyMs;

        existing.RequestCount = newCount;
        existing.TotalInputTokens += metric.TotalInputTokens;
        existing.TotalOutputTokens += metric.TotalOutputTokens;
        existing.TotalCostEgp += metric.EstimatedCostEgp;

        switch (metric.GuardrailOutcome)
        {
            case "pass": existing.GuardrailPassCount++; break;
            case "warn": existing.GuardrailWarnCount++; break;
            case "block": existing.GuardrailBlockCount++; break;
        }
        if (metric.WasRefusal) existing.RefusalCount++;
        existing.ComputedAt = DateTime.UtcNow;
    }

    private static DateTime TruncateTo(string periodType, DateTime t)
    {
        var utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return periodType switch
        {
            "hourly" => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
            "daily" => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
            "weekly" => StartOfWeek(utc),
            _ => utc,
        };
    }

    private static DateTime StartOfWeek(DateTime utc)
    {
        var diff = (7 + (int)utc.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        var monday = utc.Date.AddDays(-diff);
        return DateTime.SpecifyKind(monday, DateTimeKind.Utc);
    }

    private static Guid TryParseTenant(string tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : Guid.Empty;

    private static Guid? TryParseGuid(string value)
        => Guid.TryParse(value, out var g) ? g : null;
}

public static class AiMetricConsumerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase6AIMetricConsumer(this IServiceCollection services)
    {
        services.AddScoped<IAIMetricConsumer, AIMetricConsumer>();
        return services;
    }
}
