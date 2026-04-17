using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.TutorExposure;

/// <summary>
/// T061 (US3) — Persistence for tutor text turns.
///
/// Every student-facing turn writes two <see cref="TutorChatMessage"/>
/// rows: one <c>role=student</c> and one <c>role=tutor</c>. The tutor row
/// links back to the Phase 2 <c>AiRequestRecord</c> via
/// <see cref="TutorChatMessage.AiRequestRecordId"/> so analytics and incident
/// lookup can join the two without duplicating content.
///
/// Writes are added to the change tracker only; the caller commits the
/// surrounding unit of work (endpoint handler) so outbox + chat rows
/// persist together.
/// </summary>
public interface ITutorChatMessageRepository
{
    Task<TutorChatMessage> AppendStudentTurnAsync(
        Guid tenantId,
        Guid studentSessionId,
        int turnNumber,
        string language,
        string questionText,
        CancellationToken ct = default);

    Task<TutorChatMessage> AppendTutorTurnAsync(
        Guid tenantId,
        Guid studentSessionId,
        int turnNumber,
        string language,
        string? answerText,
        Guid? aiRequestRecordId,
        string? guardrailFinalStage,
        string finalOutcome,
        string confidenceSignal,
        IReadOnlyList<TutorTextEvidenceRef> evidenceRefs,
        CancellationToken ct = default);

    Task<int> NextTurnNumberAsync(Guid studentSessionId, CancellationToken ct = default);
}

public sealed class TutorChatMessageRepository : ITutorChatMessageRepository
{
    public const string StudentRole = "student";
    public const string TutorRole = "tutor";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public TutorChatMessageRepository(MuallimiDbContext db)
    {
        _db = db;
    }

    public Task<TutorChatMessage> AppendStudentTurnAsync(
        Guid tenantId,
        Guid studentSessionId,
        int turnNumber,
        string language,
        string questionText,
        CancellationToken ct = default)
    {
        var row = new TutorChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            TurnNumber = turnNumber,
            Role = StudentRole,
            Modality = "text",
            Language = language,
            ContentText = questionText,
            FinalOutcome = null,
            ConfidenceSignal = null,
            EvidenceRefs = "[]",
            CreatedAt = DateTime.UtcNow,
        };
        _db.TutorChatMessages.Add(row);
        return Task.FromResult(row);
    }

    public Task<TutorChatMessage> AppendTutorTurnAsync(
        Guid tenantId,
        Guid studentSessionId,
        int turnNumber,
        string language,
        string? answerText,
        Guid? aiRequestRecordId,
        string? guardrailFinalStage,
        string finalOutcome,
        string confidenceSignal,
        IReadOnlyList<TutorTextEvidenceRef> evidenceRefs,
        CancellationToken ct = default)
    {
        var row = new TutorChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            TurnNumber = turnNumber,
            Role = TutorRole,
            Modality = "text",
            Language = language,
            ContentText = answerText,
            AiRequestRecordId = aiRequestRecordId,
            GuardrailFinalStage = guardrailFinalStage,
            FinalOutcome = finalOutcome,
            ConfidenceSignal = confidenceSignal,
            EvidenceRefs = JsonSerializer.Serialize(evidenceRefs, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        };
        _db.TutorChatMessages.Add(row);
        return Task.FromResult(row);
    }

    public async Task<int> NextTurnNumberAsync(Guid studentSessionId, CancellationToken ct = default)
    {
        var highest = await _db.TutorChatMessages
            .AsNoTracking()
            .Where(m => m.StudentSessionId == studentSessionId)
            .Select(m => (int?)m.TurnNumber)
            .MaxAsync(ct);
        return (highest ?? 0) + 1;
    }
}

public static class TutorChatMessageRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3TutorChatMessageRepository(this IServiceCollection services)
    {
        services.AddScoped<ITutorChatMessageRepository, TutorChatMessageRepository>();
        return services;
    }
}
