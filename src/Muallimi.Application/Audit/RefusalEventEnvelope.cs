namespace Muallimi.Application.Audit;

/// <summary>
/// T056 (US2) — Consumer-side envelope mirroring the ai-service
/// RefusalEventPayload. Forwarded onto the `ai.tutor.refusal.recorded` queue
/// topic by ai-service when the guardrail chain terminates with a refuse or
/// block decision.
/// </summary>
public record RefusalEventEnvelope(
    Guid EventId,
    Guid RecordId,
    string CorrelationId,
    Guid SessionId,
    Guid TenantId,
    string Stage,
    string ReasonCode,
    string LocalisedReason,
    string TutorLanguage,
    string? FallbackLessonId,
    DateTime OccurredAt);

/// <summary>
/// Minimal consumer. Production wiring (queue adapter) assigns
/// <see cref="OnReceived"/>; tests plug in-memory handlers. Idempotent on
/// <c>event_id</c> replay (the persistence handler guards that).
/// </summary>
public class RefusalEventConsumer
{
    public Func<RefusalEventEnvelope, CancellationToken, Task>? OnReceived { get; set; }

    public Task HandleAsync(RefusalEventEnvelope envelope, CancellationToken ct = default)
        => OnReceived is null ? Task.CompletedTask : OnReceived(envelope, ct);
}
