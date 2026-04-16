namespace Muallimi.Application.PromptAudit;

/// <summary>
/// Emits audit events for prompt lifecycle actions (create, new version,
/// promote, archive, rollback). Matches the Phase 0 audit event baseline:
/// { actor, tenant, action, target, outcome, timestamp, correlation_id }.
/// </summary>
public class PromptAuditEventEmitter
{
    public record AuditEvent(
        string Actor,
        string? Tenant,
        string Action,
        string Target,
        string Outcome,
        DateTime Timestamp,
        string CorrelationId,
        string? Diff);

    public Func<AuditEvent, Task>? OnEmit { get; set; }

    public Task EmitAsync(AuditEvent @event, CancellationToken ct = default)
        => OnEmit is null ? Task.CompletedTask : OnEmit(@event);
}
