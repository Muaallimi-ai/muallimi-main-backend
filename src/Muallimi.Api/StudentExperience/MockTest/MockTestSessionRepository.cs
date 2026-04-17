using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.MockTest;

/// <summary>
/// T095 (US6) — <see cref="MockTestSession"/> persistence, snapshot, and
/// progress with server-truth timer columns.
///
/// The repository owns the three JSON columns and the two timer columns on
/// the mock_test_sessions table:
///   - <c>question_bank_snapshot</c>: the frozen list of quiz question
///     records (shared shape with Solve Questions) captured at start so
///     the player sees the same question set even if Phase 1 content
///     changes mid-run.
///   - <c>progress</c>: ordered answer + flag log used for resume, non-
///     repetition, and the final score summary.
///   - <c>server_started_at</c> / <c>server_deadline_at</c>: the
///     authoritative clock. The client timer is display only — all timeout
///     decisions compare <see cref="DateTime.UtcNow"/> against
///     <c>server_deadline_at</c>, so clock-manipulation on the client
///     cannot extend a mock test (see ClockManipulationTests).
///
/// The repository never re-checks tenancy itself (EF's global query filter
/// handles that) and never writes to pgvector or Phase 1 tables.
/// </summary>
public interface IMockTestSessionRepository
{
    Task<MockTestSession> CreateAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid subjectId,
        int timeLimitSeconds,
        string planTierSnapshot,
        IReadOnlyList<MockTestQuestionRecord> snapshot,
        CancellationToken ct = default);

    Task<MockTestSession?> FindAsync(Guid mockTestSessionId, CancellationToken ct = default);

    IReadOnlyList<MockTestQuestionRecord> ReadSnapshot(MockTestSession session);

    IReadOnlyList<MockTestProgressEntry> ReadProgress(MockTestSession session);

    Task RecordAnswerAsync(
        MockTestSession session,
        string questionId,
        string? chosenOptionId,
        bool isFlagged,
        CancellationToken ct = default);

    Task<MockTestSession> MarkSubmittedAsync(
        MockTestSession session,
        bool timedOut,
        double finalScorePercent,
        CancellationToken ct = default);

    Task<MockTestSession> MarkAbandonedAsync(
        MockTestSession session,
        CancellationToken ct = default);
}

public sealed record MockTestQuestionRecord(
    [property: JsonPropertyName("question_id")] string QuestionId,
    [property: JsonPropertyName("chapter_id")] string ChapterId,
    [property: JsonPropertyName("topic_id")] string TopicId,
    [property: JsonPropertyName("stem_text_ar")] string StemTextAr,
    [property: JsonPropertyName("stem_text_en")] string StemTextEn,
    [property: JsonPropertyName("options")] IReadOnlyList<MockTestOptionRecord> Options,
    [property: JsonPropertyName("correct_option_id")] string CorrectOptionId);

public sealed record MockTestOptionRecord(
    [property: JsonPropertyName("option_id")] string OptionId,
    [property: JsonPropertyName("text_ar")] string TextAr,
    [property: JsonPropertyName("text_en")] string TextEn,
    [property: JsonPropertyName("is_correct")] bool IsCorrect);

public sealed record MockTestProgressEntry(
    [property: JsonPropertyName("question_id")] string QuestionId,
    [property: JsonPropertyName("chosen_option_id")] string? ChosenOptionId,
    [property: JsonPropertyName("is_flagged")] bool IsFlagged,
    [property: JsonPropertyName("answered_at")] DateTime? AnsweredAt);

public sealed class MockTestSessionRepository : IMockTestSessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MuallimiDbContext _db;

    public MockTestSessionRepository(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<MockTestSession> CreateAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid subjectId,
        int timeLimitSeconds,
        string planTierSnapshot,
        IReadOnlyList<MockTestQuestionRecord> snapshot,
        CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var row = new MockTestSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            SubjectId = subjectId,
            QuestionBankSnapshot = JsonSerializer.Serialize(snapshot, JsonOptions),
            TimeLimitSeconds = timeLimitSeconds,
            ServerStartedAt = startedAt,
            ServerDeadlineAt = startedAt.AddSeconds(timeLimitSeconds),
            Progress = "[]",
            State = "in_progress",
            PlanTierSnapshot = planTierSnapshot,
            FinalScore = null,
        };
        _db.MockTestSessions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<MockTestSession?> FindAsync(Guid mockTestSessionId, CancellationToken ct = default)
    {
        return await _db.MockTestSessions.FirstOrDefaultAsync(m => m.Id == mockTestSessionId, ct);
    }

    public IReadOnlyList<MockTestQuestionRecord> ReadSnapshot(MockTestSession session)
    {
        if (string.IsNullOrWhiteSpace(session.QuestionBankSnapshot))
            return Array.Empty<MockTestQuestionRecord>();
        try
        {
            var records = JsonSerializer.Deserialize<List<MockTestQuestionRecord>>(
                session.QuestionBankSnapshot, JsonOptions);
            return records ?? new List<MockTestQuestionRecord>();
        }
        catch (JsonException)
        {
            return Array.Empty<MockTestQuestionRecord>();
        }
    }

    public IReadOnlyList<MockTestProgressEntry> ReadProgress(MockTestSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Progress))
            return Array.Empty<MockTestProgressEntry>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<MockTestProgressEntry>>(
                session.Progress, JsonOptions);
            return entries ?? new List<MockTestProgressEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<MockTestProgressEntry>();
        }
    }

    public async Task RecordAnswerAsync(
        MockTestSession session,
        string questionId,
        string? chosenOptionId,
        bool isFlagged,
        CancellationToken ct = default)
    {
        var progress = ReadProgress(session).ToList();
        var existing = progress.FindIndex(p =>
            string.Equals(p.QuestionId, questionId, StringComparison.Ordinal));
        var entry = new MockTestProgressEntry(
            QuestionId: questionId,
            ChosenOptionId: chosenOptionId,
            IsFlagged: isFlagged,
            AnsweredAt: chosenOptionId is null ? null : DateTime.UtcNow);
        if (existing >= 0) progress[existing] = entry;
        else progress.Add(entry);
        session.Progress = JsonSerializer.Serialize(progress, JsonOptions);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<MockTestSession> MarkSubmittedAsync(
        MockTestSession session,
        bool timedOut,
        double finalScorePercent,
        CancellationToken ct = default)
    {
        session.State = timedOut ? "timed_out" : "submitted";
        session.FinalScore = finalScorePercent;
        await _db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<MockTestSession> MarkAbandonedAsync(
        MockTestSession session,
        CancellationToken ct = default)
    {
        session.State = "abandoned";
        await _db.SaveChangesAsync(ct);
        return session;
    }
}

public static class MockTestSessionRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3MockTest(this IServiceCollection services)
    {
        services.AddScoped<IMockTestSessionRepository, MockTestSessionRepository>();
        services.AddScoped<IMockTestService, MockTestService>();
        return services;
    }
}
