using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.AiOperations;

namespace Muallimi.Api.Observability.DistributedTracing;

/// <summary>
/// T078 — Operator-scoped distributed trace endpoint per
/// observability-contract.md §Distributed Tracing.
///
/// Route: GET /api/v1/operator/traces/{correlationId}
/// Response: { correlation_id, spans: [{ span_id, service_name, action,
///   started_at, duration_ms, status, error_message, parent_span_id }] }
/// </summary>
public static class DistributedTracingEndpoints
{
    public const string Route = "/api/v1/operator/traces/{correlationId}";

    public static IEndpointRouteBuilder MapDistributedTracingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, async (
            HttpContext http,
            string correlationId,
            DistributedTraceQueryService service,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;

            var trace = await service.GetTraceAsync(correlationId, ct);
            if (trace is null)
                return Results.NotFound(new { error = $"No spans for correlation_id '{correlationId}'." });

            return Results.Ok(new
            {
                correlation_id = trace.CorrelationId,
                spans = trace.Spans.Select(s => new
                {
                    span_id = s.SpanId,
                    service_name = s.ServiceName,
                    action = s.Action,
                    started_at = s.StartedAt,
                    duration_ms = s.DurationMs,
                    status = s.Status,
                    error_message = s.ErrorMessage,
                    parent_span_id = s.ParentSpanId,
                }),
            });
        })
        .WithName("GetDistributedTrace")
        .WithTags("Observability");

        return routes;
    }
}
