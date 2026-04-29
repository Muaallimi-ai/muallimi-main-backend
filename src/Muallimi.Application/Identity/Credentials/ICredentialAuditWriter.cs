using System;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Credentials;

/// <summary>
/// Writes credential-management events to the Phase 6 audit trail with
/// the actor / target / IP / user-agent / correlation context filled
/// in. Implementations must persist append-only — credential audit
/// rows are never mutated or deleted.
///
/// Event kinds are enumerated in <see cref="CredentialAuditEventKind"/>.
/// The <see cref="CredentialAuditEvent.Payload"/> field is the place
/// for kind-specific extras (rejection reason, target tier upgrade,
/// etc.) — PII fields in the payload are masked by the underlying
/// Phase 6 writer.
/// </summary>
public interface ICredentialAuditWriter
{
    Task WriteAsync(CredentialAuditEvent evt, CancellationToken ct = default);
}

public sealed record CredentialAuditEvent
{
    public required CredentialAuditEventKind Kind { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid ActorId { get; init; }
    public required string ActorType { get; init; }
    public required Guid TargetUserId { get; init; }
    public required string CorrelationId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public object? Payload { get; init; }
}

/// <summary>
/// Stable values for <see cref="CredentialAuditEvent.ActorType"/>.
/// </summary>
public static class CredentialAuditActorTypes
{
    public const string User = "user";
    public const string System = "system";
}
