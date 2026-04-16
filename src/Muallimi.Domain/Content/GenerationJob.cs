using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Content;

public class GenerationJob
{
    public Guid JobId { get; private set; }
    public Guid LessonId { get; private set; }

    /// <summary>JSON array of asset types being produced in this job.</summary>
    public string Scope { get; private set; } = "[]";

    /// <summary>JSON array of stage records (script, format_decision, render, narration, quiz, qa_cache, auto_validation).</summary>
    public string Stages { get; private set; } = "[]";

    public GenerationJobStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? CorrelationId { get; private set; }

    /// <summary>JSON object with total cost across stages and providers.</summary>
    public string? CostSummary { get; private set; }

    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? ErrorReason { get; private set; }

    private GenerationJob() { }

    public static GenerationJob Create(Guid lessonId, string scope, string? correlationId)
    {
        return new GenerationJob
        {
            JobId = Guid.NewGuid(),
            LessonId = lessonId,
            Scope = scope,
            Status = GenerationJobStatus.Queued,
            Attempts = 0,
            CorrelationId = correlationId
        };
    }

    public void MarkRunning()
    {
        if (Status != GenerationJobStatus.Queued)
            throw new InvalidOperationException($"Cannot start running from {Status}.");
        Status = GenerationJobStatus.Running;
        StartedAt = DateTime.UtcNow;
        Attempts++;
    }

    public void UpdateStages(string stagesJson) => Stages = stagesJson;

    public void MarkCompleted(string? costSummary)
    {
        if (Status != GenerationJobStatus.Running)
            throw new InvalidOperationException($"Cannot mark completed from {Status}.");
        Status = GenerationJobStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        CostSummary = costSummary;
    }

    public void MarkFailed(string reason)
    {
        if (Status != GenerationJobStatus.Running)
            throw new InvalidOperationException($"Cannot mark failed from {Status}.");
        Status = GenerationJobStatus.Failed;
        CompletedAt = DateTime.UtcNow;
        ErrorReason = reason;
    }

    public void MarkPartialFailed(string reason, string? costSummary)
    {
        if (Status != GenerationJobStatus.Running)
            throw new InvalidOperationException($"Cannot mark partial-failed from {Status}.");
        Status = GenerationJobStatus.PartialFailed;
        CompletedAt = DateTime.UtcNow;
        ErrorReason = reason;
        CostSummary = costSummary;
    }

    public bool CanRetry(int maxAttempts = 3) => Attempts < maxAttempts;

    public void ResetForRetry()
    {
        if (Status != GenerationJobStatus.Failed && Status != GenerationJobStatus.PartialFailed)
            throw new InvalidOperationException($"Cannot retry from {Status}.");
        Status = GenerationJobStatus.Queued;
        ErrorReason = null;
        CompletedAt = null;
    }
}
