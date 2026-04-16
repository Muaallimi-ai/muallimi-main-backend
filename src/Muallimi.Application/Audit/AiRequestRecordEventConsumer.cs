namespace Muallimi.Application.Audit;

/// <summary>
/// Consumes `ai.tutor.request.recorded` messages from the local queue and
/// persists them via IAiRequestRecordRepository (wired in Infrastructure).
/// Idempotent on `record_id` replay.
/// </summary>
public class AiRequestRecordEventConsumer
{
    public Func<AiRequestRecordEnvelope, CancellationToken, Task>? OnReceived { get; set; }

    public Task HandleAsync(AiRequestRecordEnvelope envelope, CancellationToken ct = default)
        => OnReceived is null ? Task.CompletedTask : OnReceived(envelope, ct);
}

public record AiRequestRecordEnvelope(
    Guid RecordId,
    string CorrelationId,
    Guid SessionId,
    Guid TenantId,
    string CurriculumType,
    string Grade,
    string Subject,
    string TutorLanguage,
    string SessionMode,
    string StagesJson,
    string RoutingDecisionJson,
    int InputTokenCount,
    int OutputTokenCount,
    int LatencyMs,
    double? CacheMatchScore,
    string FinalOutcome,
    string? QuestionTextPreview,
    string PromptVersionsUsedJson,
    DateTime OccurredAt);
