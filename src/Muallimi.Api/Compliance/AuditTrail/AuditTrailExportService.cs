using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance.AuditTrail;

/// <summary>
/// T115 — Generates audit trail export archives (JSON or CSV) per
/// audit-trail-contract.md. Writes an AuditEntry with action_type
/// <c>audit_trail.exported</c> recording scope + correlation ID for traceability.
/// </summary>
public sealed class AuditTrailExportService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;
    private readonly AuditTrailWriter _audit;
    private readonly AuditTrailExportStore _store;

    public AuditTrailExportService(
        MuallimiDbContext db,
        AuditTrailWriter audit,
        AuditTrailExportStore store)
    {
        _db = db;
        _audit = audit;
        _store = store;
    }

    public async Task<AuditTrailExportBundle> GenerateAsync(
        AuditTrailExportRequest request,
        Guid requestedBy,
        string correlationId,
        CancellationToken ct = default)
    {
        var q = _db.AuditEntries.IgnoreQueryFilters().AsNoTracking();
        if (request.TenantId is { } tenantId)
            q = q.Where(a => a.TenantId == tenantId);
        q = q.Where(a => a.OccurredAt >= request.From && a.OccurredAt <= request.To);
        if (request.ActionTypes is { Count: > 0 } types)
            q = q.Where(a => types.Contains(a.ActionType));

        var rows = await q
            .OrderBy(a => a.OccurredAt)
            .ThenBy(a => a.AuditEntryId)
            .ToListAsync(ct);

        var format = string.Equals(request.Format, "csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "json";
        var exportRequestId = Guid.NewGuid();
        var fileName = $"audit-trail-{exportRequestId:N}.{format}";
        byte[] bytes;
        string contentType;
        if (format == "csv")
        {
            bytes = Encoding.UTF8.GetBytes(ToCsv(rows));
            contentType = "text/csv";
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows, JsonOpts));
            contentType = "application/json";
        }

        var bundle = new AuditTrailExportBundle(
            exportRequestId, fileName, contentType, bytes, rows.Count, DateTime.UtcNow);
        _store.Store(bundle);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = request.TenantId ?? Guid.Empty,
            ActorId = requestedBy,
            ActorType = "operator",
            TargetId = null,
            TargetType = "audit_trail",
            ActionType = "audit_trail.exported",
            Payload = new
            {
                export_request_id = exportRequestId,
                tenant_scope = request.TenantId?.ToString() ?? "all",
                from = request.From,
                to = request.To,
                format,
                entry_count = rows.Count,
                action_types = request.ActionTypes,
            },
            CorrelationId = correlationId,
        }, ct);

        return bundle;
    }

    private static string ToCsv(IReadOnlyList<AuditEntry> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("audit_entry_id,tenant_id,actor_id,actor_type,target_id,target_type,action_type,correlation_id,occurred_at");
        foreach (var r in rows)
        {
            sb.Append(r.AuditEntryId).Append(',');
            sb.Append(r.TenantId).Append(',');
            sb.Append(r.ActorId).Append(',');
            sb.Append(Escape(r.ActorType)).Append(',');
            sb.Append(r.TargetId?.ToString() ?? string.Empty).Append(',');
            sb.Append(Escape(r.TargetType ?? string.Empty)).Append(',');
            sb.Append(Escape(r.ActionType)).Append(',');
            sb.Append(Escape(r.CorrelationId)).Append(',');
            sb.Append(r.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}

public sealed record AuditTrailExportRequest(
    Guid? TenantId,
    DateTime From,
    DateTime To,
    IReadOnlyCollection<string>? ActionTypes,
    string? Format);
