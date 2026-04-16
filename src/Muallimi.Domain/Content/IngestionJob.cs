using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Content;

public class IngestionJob
{
    public Guid JobId { get; private set; }
    public Guid SourceId { get; private set; }
    public IngestionJobStatus Status { get; private set; }

    /// <summary>JSON array of stage records (extraction, OCR fallback, normalisation, chunking, embedding, indexing).</summary>
    public string Stages { get; private set; } = "[]";

    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? ErrorReason { get; private set; }

    private IngestionJob() { } // EF Core

    public static IngestionJob Create(Guid sourceId, string correlationId)
    {
        if (sourceId == Guid.Empty)
            throw new ArgumentException("Source ID is required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation ID is required.", nameof(correlationId));

        return new IngestionJob
        {
            JobId = Guid.NewGuid(),
            SourceId = sourceId,
            Status = IngestionJobStatus.Queued,
            CorrelationId = correlationId
        };
    }

    public void MarkProcessing()
    {
        if (Status != IngestionJobStatus.Queued)
            throw new InvalidOperationException($"Cannot start processing from status '{Status}'.");
        Status = IngestionJobStatus.Processing;
        StartedAt = DateTime.UtcNow;
    }

    public void UpdateStage(string stagesJson)
    {
        Stages = stagesJson;
    }

    public void MarkCompleted()
    {
        if (Status != IngestionJobStatus.Processing)
            throw new InvalidOperationException($"Cannot complete from status '{Status}'.");
        Status = IngestionJobStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        if (Status != IngestionJobStatus.Processing && Status != IngestionJobStatus.Queued)
            throw new InvalidOperationException($"Cannot fail from status '{Status}'.");
        Status = IngestionJobStatus.Failed;
        CompletedAt = DateTime.UtcNow;
        ErrorReason = reason;
    }
}
