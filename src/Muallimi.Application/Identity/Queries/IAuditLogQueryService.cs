using System;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Application.Identity.Dtos;

namespace Muallimi.Application.Identity.Queries;

/// <summary>
/// T109 — Contract for the admin audit-log query service. Implementation
/// lives in the Api layer (so it can talk to EF) — the interface stays
/// in Application so commands/DTO tests can mock it.
/// </summary>
public interface IAuditLogQueryService
{
    Task<AdminAuditPage> QueryAsync(AuditLogQuery query, CancellationToken ct = default);
}

public sealed record AuditLogQuery(
    Guid? TenantId,
    Guid? ActorId,
    Guid? TargetId,
    string? Category,
    string? Outcome,
    DateTime? From,
    DateTime? To,
    string? Cursor,
    int Limit);
