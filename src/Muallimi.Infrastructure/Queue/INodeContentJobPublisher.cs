namespace Muallimi.Infrastructure.Queue;

/// <summary>
/// Wire-format contract shared with the ingestion worker
/// (see <c>muallimi-document-ingestion/.../NodeContent/NodeContentMessage</c>).
/// Any field change here is a cross-repo break.
///
/// Triggered when the admin clicks a node in the Review surface and the
/// content row for that node is empty / Failed / Re-fetched. The worker
/// downloads only the page PDFs listed in <see cref="PageRefs"/> from MinIO
/// (under <c>pages/{sourceId}/page_{N}.pdf</c>) and sends them to Claude
/// with the node title as the heading anchor.
/// </summary>
public sealed record NodeContentMessage(
    Guid NodeId,
    Guid SourceId,
    string Title,
    /// <summary>
    /// PDF page indices (1-based) the node touches — verbatim from the
    /// structure tree's <c>source_page_refs</c>. The worker fetches
    /// <c>pages/{SourceId}/page_{N}.pdf</c> for each entry.
    /// </summary>
    string[] PageRefs,
    /// <summary>
    /// Source's subject (e.g. "Mathematics", "ArabicLanguage") — required
    /// so the worker can resolve the correct prompt from the registry
    /// (Stage 4). Added 2026-08-07; older messages without this field will
    /// throw on the worker side with an actionable message.
    /// </summary>
    string Subject,
    string TutorLanguage,
    string CorrelationId);

public interface INodeContentJobPublisher
{
    Task PublishAsync(NodeContentMessage message, CancellationToken ct = default);
}
