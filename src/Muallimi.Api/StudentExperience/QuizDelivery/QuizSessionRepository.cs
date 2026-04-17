using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.QuizDelivery;

/// <summary>
/// T084 (US5) — <see cref="QuizSession"/> persistence + snapshot + progress.
///
/// The repository owns the two JSON columns on the quiz_sessions table:
///   - <c>question_bank_snapshot</c>: the frozen list of quiz question records
///     (question_id, options, correct-option id, bilingual explanation) taken
///     at start so the player sees the same 10 questions even if Phase 1
///     content changes mid-session.
///   - <c>progress</c>: ordered per-question answer log used by the
///     non-repetition guard and the score summary.
///
/// The repository is a small seam between the endpoint layer and EF — it
/// never re-checks tenancy itself (EF's global query filter handles that)
/// and never writes to pgvector or Phase 1 tables.
/// </summary>
public interface IQuizSessionRepository
{
    Task<QuizSession> CreateAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid subjectId,
        Guid? chapterId,
        Guid? topicId,
        IReadOnlyList<QuizQuestionRecord> snapshot,
        CancellationToken ct = default);

    Task<QuizSession?> FindAsync(Guid quizSessionId, CancellationToken ct = default);

    IReadOnlyList<QuizQuestionRecord> ReadSnapshot(QuizSession session);

    IReadOnlyList<QuizProgressEntry> ReadProgress(QuizSession session);

    Task RecordAnswerAsync(
        QuizSession session,
        string questionId,
        string chosenOptionId,
        bool isCorrect,
        CancellationToken ct = default);

    Task MarkSubmittedAsync(QuizSession session, CancellationToken ct = default);
}

public sealed record QuizProgressEntry(
    [property: JsonPropertyName("question_id")] string QuestionId,
    [property: JsonPropertyName("chosen_option_id")] string ChosenOptionId,
    [property: JsonPropertyName("is_correct")] bool IsCorrect,
    [property: JsonPropertyName("answered_at")] DateTime AnsweredAt);

public sealed class QuizSessionRepository : IQuizSessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MuallimiDbContext _db;

    public QuizSessionRepository(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<QuizSession> CreateAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid subjectId,
        Guid? chapterId,
        Guid? topicId,
        IReadOnlyList<QuizQuestionRecord> snapshot,
        CancellationToken ct = default)
    {
        var row = new QuizSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            SubjectId = subjectId,
            ChapterId = chapterId,
            TopicId = topicId,
            QuestionBankSnapshot = JsonSerializer.Serialize(snapshot, JsonOptions),
            Progress = "[]",
            State = "in_progress",
            StartedAt = DateTime.UtcNow,
        };
        _db.QuizSessions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<QuizSession?> FindAsync(Guid quizSessionId, CancellationToken ct = default)
    {
        return await _db.QuizSessions.FirstOrDefaultAsync(q => q.Id == quizSessionId, ct);
    }

    public IReadOnlyList<QuizQuestionRecord> ReadSnapshot(QuizSession session)
    {
        if (string.IsNullOrWhiteSpace(session.QuestionBankSnapshot)) return Array.Empty<QuizQuestionRecord>();
        try
        {
            var records = JsonSerializer.Deserialize<List<QuizQuestionRecord>>(
                session.QuestionBankSnapshot, JsonOptions);
            return records ?? new List<QuizQuestionRecord>();
        }
        catch (JsonException)
        {
            return Array.Empty<QuizQuestionRecord>();
        }
    }

    public IReadOnlyList<QuizProgressEntry> ReadProgress(QuizSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Progress)) return Array.Empty<QuizProgressEntry>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<QuizProgressEntry>>(
                session.Progress, JsonOptions);
            return entries ?? new List<QuizProgressEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<QuizProgressEntry>();
        }
    }

    public async Task RecordAnswerAsync(
        QuizSession session,
        string questionId,
        string chosenOptionId,
        bool isCorrect,
        CancellationToken ct = default)
    {
        var progress = ReadProgress(session).ToList();
        progress.Add(new QuizProgressEntry(
            QuestionId: questionId,
            ChosenOptionId: chosenOptionId,
            IsCorrect: isCorrect,
            AnsweredAt: DateTime.UtcNow));
        session.Progress = JsonSerializer.Serialize(progress, JsonOptions);
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkSubmittedAsync(QuizSession session, CancellationToken ct = default)
    {
        session.State = "submitted";
        session.EndedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}

public static class QuizSessionRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3QuizDelivery(this IServiceCollection services)
    {
        services.AddScoped<IQuizSessionRepository, QuizSessionRepository>();
        services.AddScoped<IQuizQuestionBank, DeterministicQuizQuestionBank>();
        services.AddScoped<IQuizDeliveryService, QuizDeliveryService>();
        return services;
    }
}
