using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance.AuditTrail;

/// <summary>
/// T115 — Read-only audit trail query service. Supports keyset pagination over
/// the composite (OccurredAt, AuditEntryId) cursor per audit-trail-contract.md,
/// plus tenant/actor/target/action/time filters. Never updates or deletes rows.
/// </summary>
public sealed class AuditTrailQueryService
{
    private readonly MuallimiDbContext _db;

    public AuditTrailQueryService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<AuditTrailQueryResult> QueryAsync(
        AuditTrailQuery query,
        CancellationToken ct = default)
    {
        var q = _db.AuditEntries.IgnoreQueryFilters().AsNoTracking();

        if (query.TenantId is { } tenantId)
            q = q.Where(a => a.TenantId == tenantId);
        if (query.ActorId is { } actorId)
            q = q.Where(a => a.ActorId == actorId);
        if (query.TargetId is { } targetId)
            q = q.Where(a => a.TargetId == targetId);
        if (query.ActionTypes is { Count: > 0 } types)
            q = q.Where(a => types.Contains(a.ActionType));
        if (query.From is { } from)
            q = q.Where(a => a.OccurredAt >= from);
        if (query.To is { } to)
            q = q.Where(a => a.OccurredAt <= to);

        if (query.Cursor is { } cursor)
        {
            q = q.Where(a =>
                a.OccurredAt < cursor.OccurredAt ||
                (a.OccurredAt == cursor.OccurredAt && a.AuditEntryId.CompareTo(cursor.AuditEntryId) < 0));
        }

        var pageSize = Math.Clamp(query.Limit ?? 50, 1, 200);
        // Keyset pagination: fetch page + 1 to detect more rows
        var rows = await q
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.AuditEntryId)
            .Take(pageSize + 1)
            .ToListAsync(ct);

        AuditTrailCursor? next = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            next = new AuditTrailCursor(last.OccurredAt, last.AuditEntryId);
            rows = rows.Take(pageSize).ToList();
        }

        return new AuditTrailQueryResult(rows, EstimateCount(rows.Count, pageSize, next), next);
    }

    public async Task<AuditEntry?> GetByIdAsync(Guid auditEntryId, CancellationToken ct = default)
    {
        return await _db.AuditEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AuditEntryId == auditEntryId, ct);
    }

    private static int EstimateCount(int returnedRows, int pageSize, AuditTrailCursor? next)
    {
        // Local parity: PostgreSQL reltuples estimate would be used in production;
        // for in-memory/dev we return an honest lower bound based on the current
        // page size + 1 when more rows exist.
        return next is null ? returnedRows : returnedRows + 1;
    }
}

public sealed record AuditTrailQuery
{
    public Guid? TenantId { get; init; }
    public Guid? ActorId { get; init; }
    public Guid? TargetId { get; init; }
    public IReadOnlyCollection<string>? ActionTypes { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public AuditTrailCursor? Cursor { get; init; }
    public int? Limit { get; init; }
}

public sealed record AuditTrailCursor(DateTime OccurredAt, Guid AuditEntryId)
{
    public string Encode()
    {
        var payload = $"{OccurredAt.ToUniversalTime():O}|{AuditEntryId:N}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
    }

    public static AuditTrailCursor? TryDecode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var bytes = Convert.FromBase64String(raw);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var parts = text.Split('|');
            if (parts.Length != 2) return null;
            if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var occurredAt)) return null;
            if (!Guid.TryParseExact(parts[1], "N", out var id)) return null;
            return new AuditTrailCursor(occurredAt, id);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record AuditTrailQueryResult(
    IReadOnlyList<AuditEntry> Entries,
    int TotalCountEstimate,
    AuditTrailCursor? NextCursor);
