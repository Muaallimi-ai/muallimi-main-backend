using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance.DataRetention;

/// <summary>
/// T117 — Evaluates active DataRetentionPolicy rows and applies the configured
/// action (delete | anonymise | archive) to records older than the retention
/// window. Writes an AuditEntry for each batch processed and updates the
/// policy's <c>LastExecutedAt</c> + <c>RowsAffectedLastRun</c>.
///
/// Per data-retention-contract.md the archive rule moves rows to a partitioned
/// archive schema in production. In the local-parity implementation archive is
/// a no-op that records processing metadata without mutating rows, since the
/// primary purpose of the archive tier is regulatory retention.
/// </summary>
public sealed class DataRetentionService
{
    private readonly MuallimiDbContext _db;
    private readonly AuditTrailWriter _audit;

    public DataRetentionService(MuallimiDbContext db, AuditTrailWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<DataRetentionExecutionResult> ExecuteAsync(
        Guid executedBy,
        string correlationId,
        CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        var policies = await _db.DataRetentionPolicies
            .Where(p => p.IsActive)
            .ToListAsync(ct);

        var executionId = Guid.NewGuid();
        var totalRows = 0;
        var perPolicy = new List<DataRetentionPolicyOutcome>();

        foreach (var policy in policies)
        {
            var cutoff = DateTime.UtcNow.AddDays(-policy.RetentionDays);
            var rowsAffected = await ApplyAsync(policy, cutoff, ct);
            policy.LastExecutedAt = DateTime.UtcNow;
            policy.RowsAffectedLastRun = rowsAffected;
            policy.UpdatedAt = DateTime.UtcNow;
            totalRows += rowsAffected;
            perPolicy.Add(new DataRetentionPolicyOutcome(
                policy.PolicyId, policy.EntityType, policy.AnonymisationRule, rowsAffected));

            await _audit.WriteAsync(new AuditTrailEntry
            {
                TenantId = Guid.Empty,
                ActorId = executedBy,
                ActorType = "operator",
                TargetId = policy.PolicyId,
                TargetType = "data_retention_policy",
                ActionType = "data_retention.executed",
                Payload = new
                {
                    execution_id = executionId,
                    entity_type = policy.EntityType,
                    rule = policy.AnonymisationRule,
                    retention_days = policy.RetentionDays,
                    cutoff = cutoff,
                    rows_affected = rowsAffected,
                },
                CorrelationId = correlationId,
            }, ct);
        }

        await _db.SaveChangesAsync(ct);

        var duration = (int)Math.Max(1, (DateTime.UtcNow - start).TotalSeconds);
        return new DataRetentionExecutionResult(
            executionId, policies.Count, totalRows, duration, perPolicy);
    }

    private async Task<int> ApplyAsync(DataRetentionPolicy policy, DateTime cutoff, CancellationToken ct)
    {
        // Archive is a no-op in local parity: rows remain addressable for
        // regulatory retention windows via the audit export endpoint.
        if (string.Equals(policy.AnonymisationRule, "archive", StringComparison.OrdinalIgnoreCase))
            return 0;

        var shouldDelete = string.Equals(policy.AnonymisationRule, "delete", StringComparison.OrdinalIgnoreCase);
        var shouldAnonymise = string.Equals(policy.AnonymisationRule, "anonymise", StringComparison.OrdinalIgnoreCase);
        if (!shouldDelete && !shouldAnonymise) return 0;

        return policy.EntityType switch
        {
            "session_event" => shouldAnonymise
                ? await AnonymiseSessionEventsAsync(cutoff, ct)
                : await DeleteSessionEventsAsync(cutoff, ct),
            "ai_operations_metric" => shouldDelete
                ? await DeleteAsync(_db.Phase6AIOperationsMetrics.IgnoreQueryFilters().Where(m => m.OccurredAt < cutoff), ct)
                : 0,
            "notification_receipt" => shouldDelete
                ? await DeleteAsync(_db.NotificationDeliveryReceipts.IgnoreQueryFilters().Where(r => r.DispatchedAt < cutoff), ct)
                : 0,
            "audit_entry" => 0, // regulatory retention; archive rule only
            "payment_transaction" => 0, // regulatory retention; archive rule only
            "invoice" => 0, // regulatory retention; archive rule only
            "dead_letter_message" => shouldDelete
                ? await DeleteAsync(_db.ProgressIngestionDeadLetters.Where(d => d.RecordedAt < cutoff), ct)
                : 0,
            "alert_event" => shouldDelete
                ? await DeleteAsync(_db.AlertEvents.Where(e => e.FiredAt < cutoff && e.ResolutionStatus == "resolved"), ct)
                : 0,
            "incident_record" => 0, // archive rule only
            _ => 0,
        };
    }

    private async Task<int> AnonymiseSessionEventsAsync(DateTime cutoff, CancellationToken ct)
    {
        var rows = await _db.SessionEvents
            .IgnoreQueryFilters()
            .Where(e => e.CreatedAt < cutoff && e.EventPayload != "{\"anonymised\":true}")
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.EventPayload = "{\"anonymised\":true}";
            row.CurriculumScope = "{}";
        }
        return rows.Count;
    }

    private async Task<int> DeleteSessionEventsAsync(DateTime cutoff, CancellationToken ct)
    {
        var rows = await _db.SessionEvents
            .IgnoreQueryFilters()
            .Where(e => e.CreatedAt < cutoff)
            .ToListAsync(ct);
        _db.SessionEvents.RemoveRange(rows);
        return rows.Count;
    }

    private async Task<int> DeleteAsync<T>(IQueryable<T> query, CancellationToken ct) where T : class
    {
        var rows = await query.ToListAsync(ct);
        _db.Set<T>().RemoveRange(rows);
        return rows.Count;
    }
}

public sealed record DataRetentionExecutionResult(
    Guid ExecutionId,
    int PoliciesEvaluated,
    int RowsAffected,
    int DurationSeconds,
    IReadOnlyList<DataRetentionPolicyOutcome> PerPolicy);

public sealed record DataRetentionPolicyOutcome(
    Guid PolicyId,
    string EntityType,
    string Rule,
    int RowsAffected);
