using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.AiOperations.MetricAggregation;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.AiOperations;

/// <summary>
/// T069 (US3) — Phase 6 AI Operations Dashboard API surface per
/// ai-operations-contract.md. Query endpoints are operator-gated via the
/// existing <see cref="AiOperationsEndpoints.TryEnsureOperator"/> helper.
/// Mutating endpoints (create alert rule, acknowledge) write to
/// <see cref="AuditTrailWriter"/> per T076.
///
/// Routes:
///  - POST /internal/ai-ops/metrics/ingest        — local parity sink for ai-service
///  - GET  /api/v1/operator/ai-operations/overview
///  - GET  /api/v1/operator/ai-operations/tenants/{tenantId}
///  - GET  /api/v1/operator/ai-operations/guardrails
///  - GET  /api/v1/operator/ai-operations/alerts
///  - POST /api/v1/operator/ai-operations/alerts/rules
///  - POST /api/v1/operator/ai-operations/alerts/events/{alertEventId}/ack
/// </summary>
public static class Phase6AiOperationsEndpoints
{
    public const string IngestRoute = "/internal/ai-ops/metrics/ingest";
    public const string OverviewRoute = "/api/v1/operator/ai-operations/overview";
    public const string TenantRoute = "/api/v1/operator/ai-operations/tenants/{tenantId:guid}";
    public const string GuardrailsRoute = "/api/v1/operator/ai-operations/guardrails";
    public const string AlertsRoute = "/api/v1/operator/ai-operations/alerts";
    public const string AlertRuleRoute = "/api/v1/operator/ai-operations/alerts/rules";
    public const string AlertAcknowledgeRoute = "/api/v1/operator/ai-operations/alerts/events/{alertEventId:guid}/ack";

    public static void MapPhase6AiOperationsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(IngestRoute, async (
            AIMetricIngestionEvent body,
            IAIMetricConsumer consumer,
            CancellationToken ct) =>
        {
            await consumer.IngestAsync(body, ct);
            return Results.Accepted();
        });

        routes.MapGet(OverviewRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            string? period,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            var (since, now) = ResolveWindow(period);

            var metrics = await db.Phase6AIOperationsMetrics
                .IgnoreQueryFilters()
                .Where(m => m.OccurredAt >= since && m.OccurredAt <= now)
                .Select(m => new { m.OccurredAt, m.LatencyMs, m.EstimatedCostEgp, m.GuardrailOutcome, m.WasRefusal, m.ProviderName })
                .ToListAsync(ct);

            var total = metrics.Count;
            var totalCost = metrics.Sum(m => m.EstimatedCostEgp);
            var latencies = metrics.Select(m => m.LatencyMs).OrderBy(x => x).ToArray();
            var guardrail = new
            {
                pass = metrics.Count(m => m.GuardrailOutcome == "pass"),
                warn = metrics.Count(m => m.GuardrailOutcome == "warn"),
                block = metrics.Count(m => m.GuardrailOutcome == "block"),
            };
            var refusalRate = total == 0 ? 0m : Math.Round((decimal)metrics.Count(m => m.WasRefusal) * 100m / total, 2);

            var providerHealth = metrics
                .GroupBy(m => m.ProviderName)
                .Select(g => new { provider_name = g.Key, status = "healthy", last_check_at = now })
                .ToArray();

            var series = BucketTimeSeries(metrics.Select(m => (m.OccurredAt, m.LatencyMs, m.EstimatedCostEgp)), since, now);

            return Results.Ok(new
            {
                period = period ?? "24h",
                total_requests = total,
                total_cost_egp = Math.Round(totalCost, 4),
                avg_latency_ms = latencies.Length == 0 ? 0 : (int)latencies.Average(),
                p95_latency_ms = Percentile(latencies, 0.95),
                p99_latency_ms = Percentile(latencies, 0.99),
                guardrail_outcomes = guardrail,
                refusal_rate = refusalRate,
                provider_health = providerHealth,
                time_series = series,
                data_freshness_at = now,
            });
        });

        routes.MapGet(TenantRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            Guid tenantId,
            string? period,
            string? phase,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            var (since, now) = ResolveWindow(period);

            var q = db.Phase6AIOperationsMetrics
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.OccurredAt >= since && m.OccurredAt <= now);
            if (!string.IsNullOrWhiteSpace(phase)) q = q.Where(m => m.Phase == phase);

            var rows = await q.Select(m => new { m.Phase, m.PromptKey, m.PromptVersion, m.LatencyMs, m.EstimatedCostEgp, m.GuardrailOutcome, m.OccurredAt })
                              .ToListAsync(ct);

            var byPhase = rows.GroupBy(r => r.Phase).Select(g => new
            {
                phase = g.Key,
                request_count = g.Count(),
                cost_egp = Math.Round(g.Sum(x => x.EstimatedCostEgp), 4),
                avg_latency_ms = g.Any() ? (int)g.Average(x => x.LatencyMs) : 0,
            }).ToArray();

            var byPromptKey = rows.GroupBy(r => new { r.PromptKey, r.PromptVersion }).Select(g => new
            {
                prompt_key = g.Key.PromptKey,
                prompt_version = g.Key.PromptVersion,
                request_count = g.Count(),
                cost_egp = Math.Round(g.Sum(x => x.EstimatedCostEgp), 4),
                guardrail_block_rate = g.Any()
                    ? Math.Round((decimal)g.Count(x => x.GuardrailOutcome == "block") / g.Count(), 4)
                    : 0m,
            }).ToArray();

            var series = BucketTimeSeries(rows.Select(r => (r.OccurredAt, r.LatencyMs, r.EstimatedCostEgp)), since, now);

            return Results.Ok(new
            {
                tenant_id = tenantId,
                tenant_name = (string?)null,
                total_requests = rows.Count,
                total_cost_egp = Math.Round(rows.Sum(r => r.EstimatedCostEgp), 4),
                by_phase = byPhase,
                by_prompt_key = byPromptKey,
                time_series = series,
            });
        });

        routes.MapGet(GuardrailsRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            string? period,
            string? outcome,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            var (since, now) = ResolveWindow(period);
            var take = Math.Clamp(limit, 1, 200);

            var q = db.Phase6AIOperationsMetrics
                .IgnoreQueryFilters()
                .Where(m => m.OccurredAt >= since && m.OccurredAt <= now);

            var rows = await q.Select(m => new { m.PromptKey, m.GuardrailOutcome, m.WasRefusal, m.CorrelationId, m.OccurredAt }).ToListAsync(ct);

            var summary = new
            {
                pass_count = rows.Count(r => r.GuardrailOutcome == "pass"),
                warn_count = rows.Count(r => r.GuardrailOutcome == "warn"),
                block_count = rows.Count(r => r.GuardrailOutcome == "block"),
                block_rate_trend = "stable",
            };

            var blockedByPrompt = rows
                .Where(r => string.IsNullOrEmpty(outcome) || r.GuardrailOutcome == outcome)
                .GroupBy(r => r.PromptKey)
                .Select(g => new
                {
                    prompt_key = g.Key,
                    block_rate = g.Any() ? Math.Round((decimal)g.Count(r => r.GuardrailOutcome == "block") / g.Count(), 4) : 0m,
                    warn_rate = g.Any() ? Math.Round((decimal)g.Count(r => r.GuardrailOutcome == "warn") / g.Count(), 4) : 0m,
                    sample_blocked = g.Where(r => r.GuardrailOutcome == "block")
                        .Take(3)
                        .Select(r => new { correlation_id = r.CorrelationId, occurred_at = r.OccurredAt, input_summary = "[redacted]" })
                        .ToArray(),
                })
                .Take(take)
                .ToArray();

            return Results.Ok(new { summary, by_prompt_key = blockedByPrompt, next_cursor = (string?)null });
        });

        routes.MapGet(AlertsRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;

            var rules = await db.AlertRules.OrderByDescending(r => r.CreatedAt)
                .Select(r => new
                {
                    rule_id = r.RuleId,
                    rule_name = r.RuleName,
                    metric_type = r.MetricType,
                    threshold_value = r.ThresholdValue,
                    threshold_direction = r.ThresholdDirection,
                    is_active = r.IsActive,
                }).ToListAsync(ct);

            var events = await (
                from e in db.AlertEvents
                join r in db.AlertRules on e.RuleId equals r.RuleId
                orderby e.FiredAt descending
                select new
                {
                    alert_event_id = e.AlertEventId,
                    rule_name = r.RuleName,
                    triggering_value = e.TriggeringValue,
                    resolution_status = e.ResolutionStatus,
                    fired_at = e.FiredAt,
                }).Take(100).ToListAsync(ct);

            return Results.Ok(new { rules, recent_events = events });
        });

        routes.MapPost(AlertRuleRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            AuditTrailWriter audit,
            AlertRuleRequest body,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            if (string.IsNullOrWhiteSpace(body.RuleName))
                return Results.BadRequest(new { error = "rule_name is required." });
            if (!IsValidMetricType(body.MetricType))
                return Results.BadRequest(new { error = "metric_type not supported." });
            if (!IsValidDirection(body.ThresholdDirection))
                return Results.BadRequest(new { error = "threshold_direction must be above|below." });

            var actorId = ParseActor(http);
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
            var existing = await db.AlertRules.FirstOrDefaultAsync(
                r => r.RuleName == body.RuleName && r.MetricType == body.MetricType, ct);

            bool created;
            AlertRule rule;
            if (existing is null)
            {
                rule = new AlertRule
                {
                    RuleId = Guid.NewGuid(),
                    RuleName = body.RuleName,
                    MetricType = body.MetricType,
                    ThresholdValue = body.ThresholdValue,
                    ThresholdDirection = body.ThresholdDirection,
                    EvaluationWindowMin = Math.Max(1, body.EvaluationWindowMin),
                    CooldownMin = Math.Max(0, body.CooldownMin),
                    TenantScope = body.TenantScope,
                    NotificationTargets = JsonSerializer.Serialize(body.NotificationTargets ?? Array.Empty<AlertRuleTarget>()),
                    IsActive = true,
                    CreatedByOperatorId = actorId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };
                db.AlertRules.Add(rule);
                created = true;
            }
            else
            {
                rule = existing;
                rule.ThresholdValue = body.ThresholdValue;
                rule.ThresholdDirection = body.ThresholdDirection;
                rule.EvaluationWindowMin = Math.Max(1, body.EvaluationWindowMin);
                rule.CooldownMin = Math.Max(0, body.CooldownMin);
                rule.TenantScope = body.TenantScope;
                rule.NotificationTargets = JsonSerializer.Serialize(body.NotificationTargets ?? Array.Empty<AlertRuleTarget>());
                rule.UpdatedAt = DateTime.UtcNow;
                created = false;
            }
            await db.SaveChangesAsync(ct);

            // T076 — audit record (create vs update)
            await audit.WriteAsync(new AuditTrailEntry
            {
                TenantId = rule.TenantScope ?? Guid.Empty,
                ActorId = actorId,
                ActorType = "operator",
                TargetId = rule.RuleId,
                TargetType = "alert_rule",
                ActionType = created ? "alert_rule.created" : "alert_rule.modified",
                Payload = new
                {
                    rule_name = rule.RuleName,
                    metric_type = rule.MetricType,
                    threshold_value = rule.ThresholdValue,
                    threshold_direction = rule.ThresholdDirection,
                },
                CorrelationId = correlationId,
            }, ct);

            return Results.Ok(new { rule_id = rule.RuleId, created });
        });

        routes.MapPost(AlertAcknowledgeRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            AuditTrailWriter audit,
            Guid alertEventId,
            AlertEventAckRequest body,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            var evt = await db.AlertEvents.FirstOrDefaultAsync(e => e.AlertEventId == alertEventId, ct);
            if (evt is null) return Results.NotFound();

            var actorId = ParseActor(http);
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? alertEventId.ToString("N");
            evt.ResolutionStatus = "acknowledged";
            evt.ResolvedBy = actorId;
            evt.ResolvedAt = DateTime.UtcNow;
            evt.ResolutionNotes = body.Notes;
            await db.SaveChangesAsync(ct);

            // T076 — audit record for acknowledgement
            await audit.WriteAsync(new AuditTrailEntry
            {
                TenantId = Guid.Empty,
                ActorId = actorId,
                ActorType = "operator",
                TargetId = evt.AlertEventId,
                TargetType = "alert_event",
                ActionType = "alert_event.acknowledged",
                Payload = new { notes = body.Notes },
                CorrelationId = correlationId,
            }, ct);

            return Results.Ok(new { alert_event_id = evt.AlertEventId, resolution_status = evt.ResolutionStatus });
        });
    }

    private static int Percentile(int[] sortedAsc, double p)
    {
        if (sortedAsc.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p * sortedAsc.Length) - 1;
        return sortedAsc[Math.Clamp(idx, 0, sortedAsc.Length - 1)];
    }

    private static object[] BucketTimeSeries(
        IEnumerable<(DateTime OccurredAt, int LatencyMs, decimal Cost)> rows,
        DateTime since,
        DateTime now)
    {
        var bucketSize = (now - since).TotalHours <= 6
            ? TimeSpan.FromMinutes(15)
            : (now - since).TotalHours <= 48 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6);

        return rows
            .GroupBy(r => TruncateTo(r.OccurredAt, bucketSize))
            .OrderBy(g => g.Key)
            .Select(g => (object)new
            {
                timestamp = g.Key,
                request_count = g.Count(),
                cost_egp = Math.Round(g.Sum(x => x.Cost), 4),
                avg_latency_ms = (int)g.Average(x => x.LatencyMs),
            })
            .ToArray();
    }

    private static DateTime TruncateTo(DateTime t, TimeSpan bucket)
    {
        var ticks = bucket.Ticks;
        return new DateTime((t.Ticks / ticks) * ticks, DateTimeKind.Utc);
    }

    private static (DateTime Since, DateTime Now) ResolveWindow(string? period)
    {
        var now = DateTime.UtcNow;
        var span = period switch
        {
            "1h" => TimeSpan.FromHours(1),
            "6h" => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromHours(24),
        };
        return (now - span, now);
    }

    private static bool IsValidMetricType(string t)
        => t is "ai_cost" or "ai_latency" or "error_rate" or "guardrail_block_rate" or "queue_depth";

    private static bool IsValidDirection(string d)
        => string.Equals(d, "above", StringComparison.OrdinalIgnoreCase)
        || string.Equals(d, "below", StringComparison.OrdinalIgnoreCase);

    private static Guid ParseActor(HttpContext http)
    {
        var header = http.Request.Headers["X-Actor-Id"].FirstOrDefault();
        return Guid.TryParse(header, out var g) ? g : Guid.Empty;
    }
}

public sealed record AlertRuleRequest(
    string RuleName,
    string MetricType,
    decimal ThresholdValue,
    string ThresholdDirection,
    int EvaluationWindowMin,
    int CooldownMin,
    Guid? TenantScope,
    IReadOnlyList<AlertRuleTarget>? NotificationTargets);

public sealed record AlertRuleTarget(Guid OperatorId, string Channel);

public sealed record AlertEventAckRequest(string? Notes);
