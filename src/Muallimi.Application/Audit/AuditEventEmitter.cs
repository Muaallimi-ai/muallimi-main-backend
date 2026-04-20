using Microsoft.Extensions.Logging;

namespace Muallimi.Application.Audit;

/// <summary>
/// Emits audit events matching the Phase 0 audit event baseline.
/// Every sensitive action (content approval, role change, tenant access denial, etc.)
/// is logged with actor, tenant, action, target, outcome, timestamp, and correlation ID.
/// </summary>
public class AuditEventEmitter
{
    private readonly ILogger<AuditEventEmitter> _logger;

    public AuditEventEmitter(ILogger<AuditEventEmitter> logger)
    {
        _logger = logger;
    }

    public virtual void Emit(AuditEvent auditEvent)
    {
        _logger.LogInformation(
            "AUDIT: [{Category}] {Action} on {TargetType}/{TargetId} by {ActorId} in tenant {TenantId} => {Outcome} | CorrelationId={CorrelationId}",
            auditEvent.EventCategory,
            auditEvent.Action,
            auditEvent.TargetType,
            auditEvent.TargetId,
            auditEvent.ActorId,
            auditEvent.TenantId,
            auditEvent.Outcome,
            auditEvent.CorrelationId);
    }
}

public record AuditEvent
{
    public required string EventCategory { get; init; } // identity, role, tenant, content-approval, prompt, billing, provider-configuration, security
    public required string ActorId { get; init; }
    public required string TenantId { get; init; }
    public required string Action { get; init; }
    public required string TargetType { get; init; }
    public required string TargetId { get; init; }
    public required string Outcome { get; init; } // succeeded, failed, denied
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string CorrelationId { get; init; }
    public string? Reason { get; init; }
    public string? ImpersonationSessionId { get; init; }
}
