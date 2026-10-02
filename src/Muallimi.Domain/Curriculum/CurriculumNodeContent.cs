namespace Muallimi.Domain.Curriculum;

/// <summary>
/// Verbatim Arabic text + image-description payload for a single node in the
/// extracted CurriculumStructure tree. Materialised on demand by the per-node
/// content-fetch worker (Phase A Step 2): structure extraction stays cheap
/// and structure-only; body text is fetched lazily when the admin clicks a
/// node to review it.
///
/// One row per node. <see cref="NodeId"/> matches the GUID assigned to the
/// node inside <see cref="CurriculumStructure.Nodes"/> JSON at extraction
/// time. The mapping is by-value (no FK to JSON), but <see cref="SourceId"/>
/// carries a real FK to <see cref="CurriculumSource"/> so deleting a source
/// cascades all its node-content rows.
///
/// The row's lifecycle covers the entire fetch — it's created with
/// <see cref="Status"/> = <see cref="NodeContentStatus.Fetching"/> as soon as
/// the admin clicks the node, transitions to <see cref="NodeContentStatus.Ready"/>
/// when the worker delivers the markdown, or <see cref="NodeContentStatus.Failed"/>
/// with an error reason if the Claude call blew up. Polling the GET endpoint
/// returns the row in whatever state it sits in, so the frontend can show
/// spinner / content / error card from one consistent payload.
/// </summary>
public class CurriculumNodeContent
{
    public Guid NodeId { get; private set; }
    public Guid SourceId { get; private set; }

    public NodeContentStatus Status { get; private set; }

    /// <summary>
    /// Verbatim Arabic markdown for the node. Null while <see cref="Status"/>
    /// is Fetching or Failed.
    /// </summary>
    public string? Markdown { get; private set; }

    /// <summary>
    /// One- or two-sentence description of any illustration on the node's
    /// pages that carries pedagogical meaning. Null when there's no image or
    /// only decorative ones.
    /// </summary>
    public string? ImageDescription { get; private set; }

    /// <summary>SHA256 of <see cref="Markdown"/> when Ready. Stable handle
    /// for delta detection in Phase B's concept extractor.</summary>
    public string? ContentHash { get; private set; }

    /// <summary>
    /// Worker-reported failure reason when <see cref="Status"/> is Failed.
    /// Null otherwise. Surfaced verbatim in the admin's error card so the
    /// fix path is obvious (rate limit, malformed PDF, credits, etc.).
    /// </summary>
    public string? ErrorReason { get; private set; }

    public DateTime RequestedAt { get; private set; }
    public DateTime? FetchedAt { get; private set; }
    public string ModelVersion { get; private set; } = string.Empty;
    public string? CorrelationId { get; private set; }

    // ── Admin review controls (Phase A Step 2 / Stage 4) ──
    public bool IsApproved { get; private set; }
    public DateTime? ApprovedAt { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }

    public CurriculumSource? Source { get; set; }

    private CurriculumNodeContent() { } // EF Core

    /// <summary>
    /// Creates a row in the Fetching state at the moment the admin clicks the
    /// node. The worker will later upsert with the actual content.
    /// </summary>
    public static CurriculumNodeContent BeginFetch(Guid nodeId, Guid sourceId, string? correlationId)
    {
        if (nodeId == Guid.Empty) throw new ArgumentException("Node id is required.", nameof(nodeId));
        if (sourceId == Guid.Empty) throw new ArgumentException("Source id is required.", nameof(sourceId));

        return new CurriculumNodeContent
        {
            NodeId = nodeId,
            SourceId = sourceId,
            Status = NodeContentStatus.Fetching,
            RequestedAt = DateTime.UtcNow,
            CorrelationId = correlationId,
        };
    }

    /// <summary>
    /// Called when the admin re-clicks a previously-loaded node and wants a
    /// fresh result. Drops the existing markdown + approval state and puts
    /// the row back into Fetching so the worker overwrite is consistent.
    /// </summary>
    public void RestartFetch(string? correlationId)
    {
        Status = NodeContentStatus.Fetching;
        Markdown = null;
        ImageDescription = null;
        ContentHash = null;
        ErrorReason = null;
        RequestedAt = DateTime.UtcNow;
        FetchedAt = null;
        ModelVersion = string.Empty;
        CorrelationId = correlationId;
        IsApproved = false;
        ApprovedAt = null;
        ApprovedByUserId = null;
    }

    /// <summary>
    /// Worker callback for a successful fetch. Locks the row into the Ready
    /// state with the freshly-fetched payload.
    /// </summary>
    public void CompleteFetch(string markdown, string? imageDescription, string contentHash, string modelVersion)
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("Markdown is required.", nameof(markdown));
        if (string.IsNullOrWhiteSpace(contentHash)) throw new ArgumentException("Content hash is required.", nameof(contentHash));

        Status = NodeContentStatus.Ready;
        Markdown = markdown;
        ImageDescription = imageDescription;
        ContentHash = contentHash;
        FetchedAt = DateTime.UtcNow;
        ModelVersion = modelVersion ?? string.Empty;
        ErrorReason = null;
    }

    /// <summary>
    /// Worker callback for a failed fetch. The admin sees the error reason
    /// in the panel + a "Try again" button — no auto-retry on the worker side.
    /// </summary>
    public void FailFetch(string errorReason)
    {
        Status = NodeContentStatus.Failed;
        ErrorReason = string.IsNullOrWhiteSpace(errorReason) ? "Unknown error" : errorReason;
        FetchedAt = DateTime.UtcNow;
    }

    public void Approve(Guid? approverUserId)
    {
        if (Status != NodeContentStatus.Ready)
            throw new InvalidOperationException("Only Ready node content can be approved.");
        if (IsApproved) return;
        IsApproved = true;
        ApprovedAt = DateTime.UtcNow;
        ApprovedByUserId = approverUserId;
    }
}

public enum NodeContentStatus
{
    /// <summary>Fetch in flight — worker has been notified, no content yet.</summary>
    Fetching = 0,

    /// <summary>Content delivered by the worker and ready for display.</summary>
    Ready = 1,

    /// <summary>Worker reported an error; <see cref="CurriculumNodeContent.ErrorReason"/> is populated.</summary>
    Failed = 2,
}

/// <summary>
/// Admin-submitted "the extraction for this node is wrong" feedback. Captured
/// against a specific content version via <see cref="ContentHash"/> so the
/// reviewer can later see exactly which payload triggered the complaint even
/// after a Re-fetch produced a different result.
/// </summary>
public class CurriculumNodeContentReport
{
    public Guid ReportId { get; private set; }
    public Guid NodeId { get; private set; }
    public Guid SourceId { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public string Comment { get; private set; } = string.Empty;
    public DateTime ReportedAt { get; private set; }
    public Guid? ReportedByUserId { get; private set; }

    private CurriculumNodeContentReport() { } // EF Core

    public static CurriculumNodeContentReport Create(
        Guid nodeId, Guid sourceId, string contentHash, string comment, Guid? reporterUserId)
    {
        if (nodeId == Guid.Empty) throw new ArgumentException("Node id is required.", nameof(nodeId));
        if (sourceId == Guid.Empty) throw new ArgumentException("Source id is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("Comment is required.", nameof(comment));

        return new CurriculumNodeContentReport
        {
            ReportId = Guid.NewGuid(),
            NodeId = nodeId,
            SourceId = sourceId,
            ContentHash = contentHash ?? string.Empty,
            Comment = comment,
            ReportedAt = DateTime.UtcNow,
            ReportedByUserId = reporterUserId,
        };
    }
}
