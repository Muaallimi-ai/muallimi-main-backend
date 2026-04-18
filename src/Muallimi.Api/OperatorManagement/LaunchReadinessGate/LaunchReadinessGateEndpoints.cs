using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.AiOperations;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.OperatorManagement.LaunchReadinessGate;

/// <summary>
/// T124 (US9) — Launch-readiness gate endpoints per
/// operator-management-contract.md §"Launch-Readiness Gate". Operator-gated
/// via <c>X-Actor-Role: operator</c>. Evaluate and sign-off both emit audit
/// entries through <see cref="AuditTrailWriter"/>.
/// </summary>
public static class LaunchReadinessGateEndpoints
{
    public static IEndpointRouteBuilder MapLaunchReadinessGateEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/operator/launch-readiness/evaluate", EvaluateAsync);
        routes.MapGet("/api/v1/operator/launch-readiness/history", GetHistoryAsync);
        routes.MapGet("/api/v1/operator/launch-readiness/{gateId:guid}", GetGateAsync);
        routes.MapPost("/api/v1/operator/launch-readiness/{gateId:guid}/sign-off", SignOffAsync);
        return routes;
    }

    private static async Task<IResult> EvaluateAsync(
        HttpContext http,
        LaunchReadinessGateEvaluator evaluator,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var operatorId = ResolveOperatorId(http);
        var correlationId = ResolveCorrelation(http);
        var result = await evaluator.EvaluateAsync(operatorId, correlationId, ct);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> GetGateAsync(
        HttpContext http,
        MuallimiDbContext db,
        Guid gateId,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var gate = await db.LaunchReadinessGates
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.GateId == gateId, ct);
        if (gate is null) return Results.NotFound(new { error = "Gate not found." });

        var criteria = DeserializeCriteria(gate.CriteriaResults);
        return Results.Ok(new
        {
            gate_id = gate.GateId,
            overall_status = gate.OverallStatus,
            criteria_results = criteria.Select(ToCriterionDto),
            evaluated_by = gate.EvaluatedBy,
            evaluated_at = gate.EvaluatedAt,
        });
    }

    private static async Task<IResult> GetHistoryAsync(
        HttpContext http,
        MuallimiDbContext db,
        int? limit,
        string? cursor,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor) && int.TryParse(cursor, out var parsed) && parsed >= 0)
        {
            offset = parsed;
        }

        var all = await db.LaunchReadinessGates
            .AsNoTracking()
            .OrderByDescending(g => g.EvaluatedAt)
            .Skip(offset)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        var page = all.Take(pageSize).ToList();
        var next = all.Count > pageSize ? (offset + pageSize).ToString() : null;

        var evaluations = page.Select(g =>
        {
            var criteria = DeserializeCriteria(g.CriteriaResults);
            return new
            {
                gate_id = g.GateId,
                overall_status = g.OverallStatus,
                pass_count = criteria.Count(c => c.Status == "pass"),
                fail_count = criteria.Count(c => c.Status == "fail"),
                evaluated_by = g.EvaluatedBy,
                evaluated_at = g.EvaluatedAt,
            };
        });

        return Results.Ok(new { evaluations, next_cursor = next });
    }

    private static async Task<IResult> SignOffAsync(
        HttpContext http,
        MuallimiDbContext db,
        LaunchReadinessGateEvaluator evaluator,
        Guid gateId,
        LaunchReadinessSignOffBody? body,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var gate = await db.LaunchReadinessGates
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.GateId == gateId, ct);
        if (gate is null) return Results.NotFound(new { error = "Gate not found." });
        if (gate.OverallStatus != "pass")
        {
            return Results.BadRequest(new { error = "Cannot sign off a failing gate." });
        }

        var operatorId = ResolveOperatorId(http);
        var correlationId = ResolveCorrelation(http);
        await evaluator.RecordGoLiveSignOffAsync(operatorId, gateId, body?.Notes, correlationId, ct);

        return Results.Ok(new
        {
            gate_id = gateId,
            signed_off_by = operatorId,
            signed_off_at = DateTime.UtcNow,
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static object ToResponse(LaunchReadinessGateResult result) => new
    {
        gate_id = result.GateId,
        overall_status = result.OverallStatus,
        criteria_results = result.CriteriaResults.Select(ToCriterionDto),
        evaluated_by = result.EvaluatedBy,
        evaluated_at = result.EvaluatedAt,
    };

    private static object ToCriterionDto(CriterionOutcome c) => new
    {
        criterion = c.Criterion,
        name_ar = c.NameAr,
        name_en = c.NameEn,
        category = c.Category,
        status = c.Status,
        evidence_link = c.EvidenceLink,
        notes = c.Notes,
    };

    private static IReadOnlyList<CriterionOutcome> DeserializeCriteria(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<CriterionOutcome>();
        try
        {
            var list = JsonSerializer.Deserialize<List<CriterionOutcome>>(json);
            return list ?? new List<CriterionOutcome>();
        }
        catch
        {
            return Array.Empty<CriterionOutcome>();
        }
    }

    private static Guid ResolveOperatorId(HttpContext http)
    {
        var raw = http.Request.Headers["X-Operator-Id"].FirstOrDefault();
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static string ResolveCorrelation(HttpContext http)
    {
        return http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
    }
}

public sealed record LaunchReadinessSignOffBody(string? Notes);
