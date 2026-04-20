using System;

namespace Muallimi.Application.Identity.Commands;

/// <summary>
/// T154 — Commands for the US6 impersonation endpoints.
/// </summary>
public sealed record StartImpersonationCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    Guid TargetUserId,
    string Reason,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record EndImpersonationCommand(
    Guid ActorUserId,
    Guid ImpersonationSessionId,
    string CorrelationId);
