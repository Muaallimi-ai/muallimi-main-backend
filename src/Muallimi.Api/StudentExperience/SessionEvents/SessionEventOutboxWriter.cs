using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.SessionEvents;

/// <summary>
/// T013 — Transactional outbox writer for the eleven Phase 4-facing session
/// event kinds. Every state-change handler calls
/// <see cref="EnqueueAsync"/> inside the same unit of work so the outbox row
/// commits atomically with the state it describes. The dispatcher
/// (see <c>SessionEventDispatcher</c>) drains pending rows to the local
/// broker with at-least-once delivery.
///
/// Constitution rule: no facade-local generation path — this class only
/// records what happened; it never invokes guardrails or adapters.
/// </summary>
public enum SessionEventKind
{
    session_start,
    lesson_view,
    content_play,
    question_asked,
    answer_received,
    refusal,
    quiz_answered,
    mock_test,
    homework_help_used,
    whiteboard_session,
    session_end,
}

public record CurriculumScope(
    string? CurriculumType,
    string? Grade,
    Guid? SubjectId,
    Guid? ChapterId,
    Guid? TopicId,
    Guid? LessonId);

public interface ISessionEventOutboxWriter
{
    /// <summary>
    /// Adds a pending outbox row to the current <see cref="MuallimiDbContext"/>
    /// change tracker. Caller is responsible for the surrounding
    /// <c>SaveChangesAsync</c> so the event and the originating state change
    /// commit together.
    /// </summary>
    Task<Guid> EnqueueAsync(
        SessionEventKind kind,
        Guid tenantId,
        Guid studentSessionId,
        Guid correlationId,
        object payload,
        CurriculumScope curriculumScope,
        string planTierSnapshot,
        CancellationToken ct = default);
}

public sealed class SessionEventOutboxWriter : ISessionEventOutboxWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public SessionEventOutboxWriter(MuallimiDbContext db)
    {
        _db = db;
    }

    public Task<Guid> EnqueueAsync(
        SessionEventKind kind,
        Guid tenantId,
        Guid studentSessionId,
        Guid correlationId,
        object payload,
        CurriculumScope curriculumScope,
        string planTierSnapshot,
        CancellationToken ct = default)
    {
        var row = new SessionEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            CorrelationId = correlationId,
            EventKind = kind.ToString(),
            EventPayload = JsonSerializer.Serialize(payload, JsonOptions),
            CurriculumScope = JsonSerializer.Serialize(curriculumScope, JsonOptions),
            PlanTierSnapshot = planTierSnapshot,
            CreatedAt = DateTime.UtcNow,
            DispatchedAt = null,
            DispatchAttempts = 0,
            DispatchState = "pending",
        };
        _db.SessionEvents.Add(row);
        return Task.FromResult(row.Id);
    }
}

public static class SessionEventOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3SessionEventOutbox(this IServiceCollection services)
    {
        services.AddScoped<ISessionEventOutboxWriter, SessionEventOutboxWriter>();
        return services;
    }
}
