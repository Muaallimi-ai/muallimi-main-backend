using System;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Domain.Identity.Events;

/// <summary>
/// Phase 9 domain events. Captured by the application layer and fanned
/// out to the audit emitter + downstream consumers (correlation-scoped
/// fire-and-forget). One record type per event — consumers switch on
/// the concrete type.
/// </summary>
public interface IIdentityDomainEvent
{
    Guid TenantId { get; }
    string CorrelationId { get; }
    DateTime OccurredAt { get; }
}

public sealed record UserRegisteredEvent(
    Guid TenantId,
    Guid UserId,
    AccountType AccountType,
    string? Email,
    string? Username,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record UserLoggedInEvent(
    Guid TenantId,
    Guid UserId,
    Guid SessionId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record UserLoggedOutEvent(
    Guid TenantId,
    Guid UserId,
    Guid SessionId,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record PasswordChangedEvent(
    Guid TenantId,
    Guid UserId,
    bool InitiatedByReset,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record EmailVerifiedEvent(
    Guid TenantId,
    Guid UserId,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record TwoFactorEnabledEvent(
    Guid TenantId,
    Guid UserId,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record RoleGrantedEvent(
    Guid TenantId,
    Guid UserId,
    Guid RoleId,
    string RoleName,
    Guid GrantedBy,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record RoleRevokedEvent(
    Guid TenantId,
    Guid UserId,
    Guid RoleId,
    string RoleName,
    Guid RevokedBy,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record AccountSuspendedEvent(
    Guid TenantId,
    Guid UserId,
    Guid SuspendedBy,
    string Reason,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record ImpersonationStartedEvent(
    Guid TenantId,
    Guid ImpersonatorId,
    Guid TargetUserId,
    Guid ImpersonationSessionId,
    string Reason,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;

public sealed record ImpersonationEndedEvent(
    Guid TenantId,
    Guid ImpersonatorId,
    Guid TargetUserId,
    Guid ImpersonationSessionId,
    string CorrelationId,
    DateTime OccurredAt) : IIdentityDomainEvent;
