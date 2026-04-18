using Microsoft.EntityFrameworkCore;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Observability.DistributedTracing;

/// <summary>
/// T077 — Aggregates already-captured request records, refusal events, and
/// operational events that share a correlation ID into a span-based trace
/// view per observability-contract.md. Local-parity: we read from existing
/// DB-backed stores (AI request records, Phase 6 AI metrics, Phase 6
/// operational events, alert events, audit entries) rather than a remote
/// tracing backend, so investigations work without Seq/Jaeger.
/// </summary>
public class DistributedTraceQueryService
{
    private readonly MuallimiDbContext _db;

    public DistributedTraceQueryService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<DistributedTrace?> GetTraceAsync(string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return null;

        var aiRequests = await _db.AiRequestRecords.AsNoTracking()
            .Where(r => r.CorrelationId == correlationId)
            .OrderBy(r => r.OccurredAt)
            .Select(r => new
            {
                r.RecordId,
                r.OccurredAt,
                r.LatencyMs,
                r.FinalOutcome,
                r.SessionMode,
            })
            .ToListAsync(ct);

        var phase6Metrics = await _db.Phase6AIOperationsMetrics.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => m.CorrelationId == correlationId)
            .OrderBy(m => m.OccurredAt)
            .Select(m => new
            {
                m.MetricId,
                m.OccurredAt,
                m.Phase,
                m.PromptKey,
                m.LatencyMs,
                m.GuardrailOutcome,
                m.WasRefusal,
            })
            .ToListAsync(ct);

        var opEvents = await _db.Phase6OperationalEvents.AsNoTracking()
            .Where(e => e.CorrelationId == correlationId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new
            {
                e.EventId,
                e.EventKind,
                e.OccurredAt,
            })
            .ToListAsync(ct);

        var audits = await _db.AuditEntries.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(a => a.CorrelationId == correlationId)
            .OrderBy(a => a.OccurredAt)
            .Select(a => new
            {
                a.AuditEntryId,
                a.ActionType,
                a.OccurredAt,
                a.ActorType,
            })
            .ToListAsync(ct);

        var spans = new List<TraceSpan>();

        foreach (var r in aiRequests)
        {
            spans.Add(new TraceSpan
            {
                SpanId = r.RecordId.ToString("N"),
                ServiceName = "ai-service",
                Action = string.IsNullOrEmpty(r.SessionMode) ? "ai.request" : $"ai.request:{r.SessionMode}",
                StartedAt = r.OccurredAt,
                DurationMs = r.LatencyMs,
                Status = string.Equals(r.FinalOutcome, "answered", StringComparison.OrdinalIgnoreCase) ? "ok" : "error",
                ErrorMessage = string.Equals(r.FinalOutcome, "answered", StringComparison.OrdinalIgnoreCase) ? null : r.FinalOutcome,
                ParentSpanId = null,
            });
        }

        foreach (var m in phase6Metrics)
        {
            var status = m.WasRefusal || string.Equals(m.GuardrailOutcome, "block", StringComparison.OrdinalIgnoreCase)
                ? "error"
                : "ok";
            spans.Add(new TraceSpan
            {
                SpanId = m.MetricId.ToString("N"),
                ServiceName = "ai-service",
                Action = $"ai.metric:{m.Phase}:{m.PromptKey}",
                StartedAt = m.OccurredAt,
                DurationMs = m.LatencyMs,
                Status = status,
                ErrorMessage = status == "error" ? m.GuardrailOutcome : null,
                ParentSpanId = null,
            });
        }

        foreach (var e in opEvents)
        {
            spans.Add(new TraceSpan
            {
                SpanId = e.EventId.ToString("N"),
                ServiceName = "main-backend",
                Action = $"operational_event:{e.EventKind}",
                StartedAt = e.OccurredAt,
                DurationMs = 0,
                Status = "ok",
                ErrorMessage = null,
                ParentSpanId = null,
            });
        }

        foreach (var a in audits)
        {
            spans.Add(new TraceSpan
            {
                SpanId = a.AuditEntryId.ToString("N"),
                ServiceName = "main-backend",
                Action = $"audit:{a.ActionType}",
                StartedAt = a.OccurredAt,
                DurationMs = 0,
                Status = "ok",
                ErrorMessage = null,
                ParentSpanId = null,
            });
        }

        if (spans.Count == 0) return null;

        spans = spans.OrderBy(s => s.StartedAt).ToList();

        // Parent-span inference: first span becomes the synthetic root;
        // subsequent spans link to the earliest span that started before them.
        if (spans.Count > 1)
        {
            var rootSpanId = spans[0].SpanId;
            for (var i = 1; i < spans.Count; i++)
            {
                spans[i] = spans[i] with { ParentSpanId = rootSpanId };
            }
        }

        return new DistributedTrace
        {
            CorrelationId = correlationId,
            Spans = spans,
        };
    }
}

public sealed record DistributedTrace
{
    public required string CorrelationId { get; init; }
    public required IReadOnlyList<TraceSpan> Spans { get; init; }
}

public sealed record TraceSpan
{
    public required string SpanId { get; init; }
    public required string ServiceName { get; init; }
    public required string Action { get; init; }
    public required DateTime StartedAt { get; init; }
    public required int DurationMs { get; init; }
    public required string Status { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ParentSpanId { get; init; }
}
