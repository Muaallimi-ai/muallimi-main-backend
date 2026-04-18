using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.AiOperations;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance.DataRetention;

/// <summary>
/// T118 — Retention policy management endpoints per data-retention-contract.md.
/// Operator-gated. Updates write an AuditEntry; the execute endpoint runs
/// <see cref="DataRetentionService"/> synchronously and returns the summary.
/// </summary>
public static class DataRetentionEndpoints
{
    public static IEndpointRouteBuilder MapDataRetentionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/operator/data-retention/policies", ListAsync);
        routes.MapPut("/api/v1/operator/data-retention/policies/{policyId:guid}", UpdateAsync);
        routes.MapPost("/api/v1/operator/data-retention/execute", ExecuteAsync);
        return routes;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;

        var rows = await db.DataRetentionPolicies
            .AsNoTracking()
            .OrderBy(p => p.EntityType)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            policies = rows.Select(p => new
            {
                policy_id = p.PolicyId,
                entity_type = p.EntityType,
                retention_days = p.RetentionDays,
                anonymisation_rule = p.AnonymisationRule,
                is_active = p.IsActive,
                last_executed_at = p.LastExecutedAt,
                rows_affected_last_run = p.RowsAffectedLastRun,
            }),
        });
    }

    private static async Task<IResult> UpdateAsync(
        Guid policyId,
        HttpContext http,
        MuallimiDbContext db,
        AuditTrailWriter audit,
        DataRetentionPolicyUpdateInput input,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        if (input.RetentionDays < 30)
            return Results.BadRequest(new { error = "retention_days_below_minimum", minimum = 30 });
        if (input.AnonymisationRule is not ("delete" or "anonymise" or "archive"))
            return Results.BadRequest(new { error = "invalid_rule" });

        var policy = await db.DataRetentionPolicies.FirstOrDefaultAsync(p => p.PolicyId == policyId, ct);
        if (policy is null) return Results.NotFound();

        var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        Guid.TryParse(http.Request.Headers["X-Actor-Id"].FirstOrDefault(), out var actorId);

        var before = new
        {
            retention_days = policy.RetentionDays,
            anonymisation_rule = policy.AnonymisationRule,
            is_active = policy.IsActive,
        };

        policy.RetentionDays = input.RetentionDays;
        policy.AnonymisationRule = input.AnonymisationRule;
        policy.IsActive = input.IsActive;
        policy.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var auditEntryId = Guid.NewGuid();
        await audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = Guid.Empty,
            ActorId = actorId,
            ActorType = "operator",
            TargetId = policy.PolicyId,
            TargetType = "data_retention_policy",
            ActionType = "data_retention.policy_updated",
            Payload = new { before, after = new { policy.RetentionDays, policy.AnonymisationRule, policy.IsActive } },
            CorrelationId = correlationId,
        }, ct);

        return Results.Ok(new
        {
            policy_id = policy.PolicyId,
            entity_type = policy.EntityType,
            retention_days = policy.RetentionDays,
            anonymisation_rule = policy.AnonymisationRule,
            audit_entry_id = auditEntryId,
        });
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext http,
        DataRetentionService service,
        CancellationToken ct)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
        var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        Guid.TryParse(http.Request.Headers["X-Actor-Id"].FirstOrDefault(), out var actorId);

        var result = await service.ExecuteAsync(actorId, correlationId, ct);
        return Results.Ok(new
        {
            execution_id = result.ExecutionId,
            policies_evaluated = result.PoliciesEvaluated,
            rows_affected = result.RowsAffected,
            duration_seconds = result.DurationSeconds,
            per_policy = result.PerPolicy.Select(o => new
            {
                policy_id = o.PolicyId,
                entity_type = o.EntityType,
                rule = o.Rule,
                rows_affected = o.RowsAffected,
            }),
        });
    }
}

public sealed record DataRetentionPolicyUpdateInput(
    int RetentionDays,
    string AnonymisationRule,
    bool IsActive);
