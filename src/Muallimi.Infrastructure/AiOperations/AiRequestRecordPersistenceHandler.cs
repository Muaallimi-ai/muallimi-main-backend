using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Audit;
using Muallimi.Domain.AiOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.AiOperations;

/// <summary>
/// T036 (US1) — Persists <c>ai.tutor.request.recorded</c> envelopes into the
/// <c>ai_request_records</c> table. Idempotent on record_id (replay is a
/// no-op). The orchestrator in ai-service emits the envelope; the queue
/// consumer (<see cref="AiRequestRecordEventConsumer"/>) forwards each
/// envelope into this handler.
/// </summary>
public class AiRequestRecordPersistenceHandler
{
    private readonly MuallimiDbContext _db;

    public AiRequestRecordPersistenceHandler(MuallimiDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task HandleAsync(AiRequestRecordEnvelope envelope, CancellationToken ct = default)
    {
        var exists = await _db.AiRequestRecords
            .AsNoTracking()
            .AnyAsync(r => r.RecordId == envelope.RecordId, ct);
        if (exists)
            return;

        var record = new AiRequestRecord
        {
            RecordId = envelope.RecordId,
            CorrelationId = envelope.CorrelationId,
            SessionId = envelope.SessionId,
            TenantId = envelope.TenantId,
            CurriculumType = envelope.CurriculumType,
            Grade = envelope.Grade,
            Subject = envelope.Subject,
            TutorLanguage = envelope.TutorLanguage,
            SessionMode = envelope.SessionMode,
            Stages = envelope.StagesJson,
            RoutingDecision = envelope.RoutingDecisionJson,
            InputTokenCount = envelope.InputTokenCount,
            OutputTokenCount = envelope.OutputTokenCount,
            LatencyMs = envelope.LatencyMs,
            CacheMatchScore = envelope.CacheMatchScore,
            FinalOutcome = envelope.FinalOutcome,
            QuestionTextPreview = envelope.QuestionTextPreview,
            PromptVersionsUsed = envelope.PromptVersionsUsedJson,
            OccurredAt = envelope.OccurredAt,
        };
        _db.AiRequestRecords.Add(record);
        await _db.SaveChangesAsync(ct);
    }
}
