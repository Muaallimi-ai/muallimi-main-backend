namespace Muallimi.Infrastructure.Queue;

/// <summary>
/// Wire-format contract shared with the ingestion worker
/// (see <c>muallimi-document-ingestion/.../UploadConsumer.IngestionMessage</c>).
/// Any field change here is a cross-repo break and must pass the Phase 0
/// contract catalogue review.
/// </summary>
public sealed record IngestionMessage(
    Guid JobId,
    Guid SourceId,
    string StorageKey,
    string CurriculumType,
    string Grade,
    string Subject,
    string TutorLanguage,
    string AcademicYear,
    string FileFormat,
    string ContentHash,
    string CorrelationId,
    // When true the worker runs the Claude structure extractor only and
    // stops before chunking + embedding. This is the path used by the
    // MVP stage-separated flow where extraction is a manually triggered
    // step. False (legacy default) keeps the original end-to-end pipeline.
    bool ExtractOnly = false);

public interface IIngestionJobPublisher
{
    Task PublishAsync(IngestionMessage message, CancellationToken ct = default);
}
