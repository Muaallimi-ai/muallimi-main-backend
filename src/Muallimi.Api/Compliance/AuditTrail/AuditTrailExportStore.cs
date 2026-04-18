using System.Collections.Concurrent;

namespace Muallimi.Api.Compliance.AuditTrail;

/// <summary>
/// T115 — In-memory export bundle cache for local-parity audit trail exports.
/// Holds the generated blob keyed by export_request_id until retrieved. In
/// production this would be backed by blob storage with signed URLs, but the
/// local mode writes directly into memory so the operator download endpoint
/// can stream the file without a managed cloud dependency.
/// </summary>
public sealed class AuditTrailExportStore
{
    private readonly ConcurrentDictionary<Guid, AuditTrailExportBundle> _bundles = new();

    public void Store(AuditTrailExportBundle bundle) => _bundles[bundle.ExportRequestId] = bundle;

    public bool TryGet(Guid exportRequestId, out AuditTrailExportBundle? bundle)
    {
        if (_bundles.TryGetValue(exportRequestId, out var found))
        {
            bundle = found;
            return true;
        }
        bundle = null;
        return false;
    }
}

public sealed record AuditTrailExportBundle(
    Guid ExportRequestId,
    string FileName,
    string ContentType,
    byte[] Bytes,
    int EntryCount,
    DateTime GeneratedAt);
