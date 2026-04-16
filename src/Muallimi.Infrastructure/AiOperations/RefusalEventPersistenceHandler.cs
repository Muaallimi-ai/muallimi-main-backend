using Muallimi.Application.AiOperations;
using Muallimi.Application.Audit;
using Muallimi.Domain.AiOperations;

namespace Muallimi.Infrastructure.AiOperations;

/// <summary>
/// T056 (US2) — Persists `ai.tutor.refusal.recorded` envelopes via
/// <see cref="IRefusalEventRepository"/>. Idempotent on <c>event_id</c>:
/// replaying the same envelope is a no-op. The row is linked to the matching
/// <c>AiRequestRecord</c> via <c>record_id</c>; callers should tolerate
/// temporary ordering (the RefusalEvent may arrive before or after the
/// AiRequestRecord, both orders are safe).
/// </summary>
public class RefusalEventPersistenceHandler
{
    private readonly IRefusalEventRepository _repository;

    public RefusalEventPersistenceHandler(IRefusalEventRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task HandleAsync(RefusalEventEnvelope envelope, CancellationToken ct = default)
    {
        if (await _repository.ExistsAsync(envelope.EventId, ct))
            return;

        var row = new RefusalEvent
        {
            EventId = envelope.EventId,
            RecordId = envelope.RecordId,
            Stage = envelope.Stage,
            ReasonCode = envelope.ReasonCode,
            LocalisedReason = envelope.LocalisedReason,
            TutorLanguage = envelope.TutorLanguage,
            OccurredAt = envelope.OccurredAt,
        };
        await _repository.AddAsync(row, ct);
        await _repository.SaveChangesAsync(ct);
    }
}
