using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Compliance;

/// <summary>
/// T115 — Audit trail export generates JSON/CSV bundles, stores them for
/// download, and writes an <c>audit_trail.exported</c> AuditEntry.
/// </summary>
public class AuditTrailExportTests
{
    [Fact]
    public async Task Export_json_produces_retrievable_bundle_and_audit_entry()
    {
        using var db = Phase6TestDbContextFactory.Create();
        var tenant = Guid.NewGuid();
        db.AuditEntries.Add(new AuditEntry
        {
            AuditEntryId = Guid.NewGuid(),
            TenantId = tenant,
            ActorId = Guid.NewGuid(),
            ActorType = "operator",
            ActionType = "subscription.created",
            CorrelationId = "corr-1",
            OccurredAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var store = new AuditTrailExportStore();
        var writer = new AuditTrailWriter(db);
        var svc = new AuditTrailExportService(db, writer, store);

        var bundle = await svc.GenerateAsync(
            new AuditTrailExportRequest(tenant, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, null, "json"),
            requestedBy: Guid.NewGuid(),
            correlationId: "corr-export-1");

        Assert.True(bundle.Bytes.Length > 0);
        Assert.Equal("application/json", bundle.ContentType);
        Assert.Equal(1, bundle.EntryCount);
        Assert.True(store.TryGet(bundle.ExportRequestId, out var stored));
        Assert.NotNull(stored);

        var auditRows = await db.AuditEntries.ToListAsync();
        Assert.Contains(auditRows, a => a.ActionType == "audit_trail.exported");
    }

    [Fact]
    public async Task Export_csv_emits_header_row()
    {
        using var db = Phase6TestDbContextFactory.Create();
        var store = new AuditTrailExportStore();
        var writer = new AuditTrailWriter(db);
        var svc = new AuditTrailExportService(db, writer, store);

        var bundle = await svc.GenerateAsync(
            new AuditTrailExportRequest(null, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, null, "csv"),
            requestedBy: Guid.NewGuid(),
            correlationId: "corr-csv");

        Assert.Equal("text/csv", bundle.ContentType);
        var text = System.Text.Encoding.UTF8.GetString(bundle.Bytes);
        Assert.Contains("audit_entry_id", text);
    }
}
