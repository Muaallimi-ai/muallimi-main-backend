using System;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// ImpersonationSession — super-admin or platform-operator elevates into
/// a target user for at most 1 hour. A mandatory <see cref="Reason"/> is
/// captured and every audit event emitted during the session is tagged
/// with <see cref="Id"/> via the audit event correlation fields.
/// </summary>
public class ImpersonationSession
{
    public const int DefaultMaxDurationHours = 1;

    public Guid Id { get; set; }
    public Guid ImpersonatorId { get; set; }
    public Guid TargetUserId { get; set; }
    public Guid TargetTenantId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(DefaultMaxDurationHours);
    public DateTime? EndedAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;

    public bool IsActive =>
        EndedAt is null && ExpiresAt > DateTime.UtcNow;

    public void End()
    {
        EndedAt ??= DateTime.UtcNow;
    }
}
