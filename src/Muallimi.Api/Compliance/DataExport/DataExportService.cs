using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance.DataExport;

/// <summary>
/// T093 — Generates data-subject export archives. Produces a ZIP containing
/// one JSON file per entity type with all personal data for the subject.
/// Writes an audit entry on archive generation (T098).
/// </summary>
public interface IDataExportService
{
    Task<DataExportArchive> GenerateAsync(
        Guid tenantId,
        string targetScope,
        Guid targetId,
        Guid requestedBy,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record DataExportArchive(
    string FileName,
    byte[] ZipBytes,
    string ContentType,
    IReadOnlyList<string> Entries);

public sealed class DataExportService : IDataExportService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;
    private readonly AuditTrailWriter _audit;
    private readonly ILogger<DataExportService> _logger;

    public DataExportService(
        MuallimiDbContext db,
        AuditTrailWriter audit,
        ILogger<DataExportService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task<DataExportArchive> GenerateAsync(
        Guid tenantId,
        string targetScope,
        Guid targetId,
        Guid requestedBy,
        string correlationId,
        CancellationToken ct = default)
    {
        var entries = new List<string>();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteManifestAsync(archive, tenantId, targetScope, targetId, requestedBy, correlationId, ct);
            entries.Add("manifest.json");

            if (string.Equals(targetScope, "student", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(archive, "student_profile.json",
                    await _db.StudentProfiles.IgnoreQueryFilters()
                        .Where(s => s.Id == targetId).ToListAsync(ct), ct);
                entries.Add("student_profile.json");

                await WriteJsonAsync(archive, "student_sessions.json",
                    await _db.StudentSessions.IgnoreQueryFilters()
                        .Where(s => s.StudentProfileId == targetId).ToListAsync(ct), ct);
                entries.Add("student_sessions.json");
            }

            if (string.Equals(targetScope, "tenant", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(archive, "subscriptions.json",
                    await _db.Subscriptions.IgnoreQueryFilters()
                        .Where(s => s.TenantId == tenantId).ToListAsync(ct), ct);
                entries.Add("subscriptions.json");

                await WriteJsonAsync(archive, "invoices.json",
                    await _db.Invoices.IgnoreQueryFilters()
                        .Where(i => i.TenantId == tenantId).ToListAsync(ct), ct);
                entries.Add("invoices.json");

                await WriteJsonAsync(archive, "payment_transactions.json",
                    await _db.PaymentTransactions.IgnoreQueryFilters()
                        .Where(p => p.TenantId == tenantId).ToListAsync(ct), ct);
                entries.Add("payment_transactions.json");
            }

            await WriteJsonAsync(archive, "audit_entries.json",
                await _db.AuditEntries.IgnoreQueryFilters()
                    .Where(a => a.TenantId == tenantId && (a.TargetId == targetId || a.ActorId == targetId))
                    .OrderBy(a => a.OccurredAt)
                    .ToListAsync(ct), ct);
            entries.Add("audit_entries.json");
        }

        var bytes = ms.ToArray();
        var fileName = $"data-export-{targetScope}-{targetId:N}.zip";

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = tenantId,
            ActorId = requestedBy,
            ActorType = "operator",
            TargetId = targetId,
            TargetType = targetScope,
            ActionType = "export_request",
            Payload = new { kind = "data_export_generated", file_name = fileName, entries },
            CorrelationId = correlationId,
        }, ct);

        return new DataExportArchive(fileName, bytes, "application/zip", entries);
    }

    private static async Task WriteManifestAsync(
        ZipArchive archive,
        Guid tenantId,
        string targetScope,
        Guid targetId,
        Guid requestedBy,
        string correlationId,
        CancellationToken ct)
    {
        var manifest = new
        {
            tenant_id = tenantId,
            target_scope = targetScope,
            target_id = targetId,
            requested_by = requestedBy,
            correlation_id = correlationId,
            generated_at = DateTime.UtcNow,
            schema_version = "1.0.0",
        };
        await WriteJsonAsync(archive, "manifest.json", manifest, ct);
    }

    private static async Task WriteJsonAsync(ZipArchive archive, string name, object payload, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        await stream.WriteAsync(bytes, 0, bytes.Length, ct);
    }
}
