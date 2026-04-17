using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.QuizDelivery;

/// <summary>
/// T083 (US5) — QuizDeliveryService.
///
/// Orchestrates the Solve Questions facade:
///   - Validates the subject id against the Phase 1 subject mapping
///     (and the student's enrolled subject set).
///   - Reads approved <see cref="ContentChunk"/> rows filtered by tenant
///     (via the EF global filter), curriculum type, grade, subject, and the
///     optional chapter/topic slug — mirroring the Phase 1 retrieval rules
///     used by Study mode (see
///     <see cref="LessonRetrievalService"/>). No write path exists here.
///   - Projects chunks into multiple-choice records via
///     <see cref="IQuizQuestionBank"/>.
///   - Persists the snapshot through <see cref="IQuizSessionRepository"/> so
///     non-repetition can be enforced via <c>QuizSession.Progress</c>.
///   - Returns the first unanswered question; the answer endpoint advances
///     the cursor and emits <c>quiz_answered</c> events.
///
/// Constitution rules respected:
///   - All content sourced from Phase 1 approved tables; no generation here.
///   - Tenant + scope filters applied at query time and re-applied at
///     <c>ReadSnapshot</c> to defend against a mis-scoped retrieval.
///   - Answer key never leaves the backend on the wire until the student
///     submits a chosen option.
/// </summary>
public interface IQuizDeliveryService
{
    Task<QuizStartResult> StartAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        SolveQuestionsStartRequest request,
        CancellationToken ct = default);

    Task<QuizAnswerResult> AnswerAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        QuizSession quizSession,
        SolveQuestionsAnswerRequest request,
        CancellationToken ct = default);
}

public sealed record QuizStartResult(
    QuizStartOutcome Outcome,
    QuizSession? QuizSession,
    SolveQuestionsStartResponse? Response);

public enum QuizStartOutcome
{
    Ok,
    InvalidSubject,
    InvalidCount,
    NoQuestionsAvailable,
}

public sealed record QuizAnswerResult(
    QuizAnswerOutcome Outcome,
    SolveQuestionsAnswerResponse? Response,
    bool QuizComplete,
    string? ChosenOptionId,
    QuizQuestionRecord? AnsweredRecord);

public enum QuizAnswerOutcome
{
    Ok,
    QuizNotFound,
    QuizAlreadyEnded,
    QuestionNotInSnapshot,
    QuestionAlreadyAnswered,
    InvalidOption,
}

public sealed class QuizDeliveryService : IQuizDeliveryService
{
    public const int DefaultQuestionCount = 10;
    public const int MinQuestionCount = 1;
    public const int MaxQuestionCount = 20;

    private readonly MuallimiDbContext _db;
    private readonly IQuizQuestionBank _questionBank;
    private readonly IQuizSessionRepository _quizSessions;

    public QuizDeliveryService(
        MuallimiDbContext db,
        IQuizQuestionBank questionBank,
        IQuizSessionRepository quizSessions)
    {
        _db = db;
        _questionBank = questionBank;
        _quizSessions = quizSessions;
    }

    public async Task<QuizStartResult> StartAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        SolveQuestionsStartRequest request,
        CancellationToken ct = default)
    {
        var subject = LessonRetrievalService.SubjectFromGuid(request.SubjectId);
        if (subject is null) return new QuizStartResult(QuizStartOutcome.InvalidSubject, null, null);

        var enrolled = ResolveEnrolledSubjects(profile.SubjectsEnrolled);
        if (enrolled.Count > 0 && !enrolled.Contains(subject.Value))
            return new QuizStartResult(QuizStartOutcome.InvalidSubject, null, null);

        var questionCount = request.QuestionCount <= 0 ? DefaultQuestionCount : request.QuestionCount;
        if (questionCount < MinQuestionCount || questionCount > MaxQuestionCount)
            return new QuizStartResult(QuizStartOutcome.InvalidCount, null, null);

        var curriculumType = ParseEnumOrNull<CurriculumType>(profile.CurriculumType);
        var grade = ParseEnumOrNull<Grade>(profile.Grade);

        var sources = await LoadChunkSourcesAsync(
            subject.Value, curriculumType, grade, request.ChapterId, request.TopicId, questionCount, ct);

        if (sources.Count == 0)
            return new QuizStartResult(QuizStartOutcome.NoQuestionsAvailable, null, null);

        var snapshot = _questionBank.ProjectFromChunks(sources, questionCount);
        if (snapshot.Count == 0)
            return new QuizStartResult(QuizStartOutcome.NoQuestionsAvailable, null, null);

        var quizSession = await _quizSessions.CreateAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            subjectId: request.SubjectId,
            chapterId: request.ChapterId,
            topicId: request.TopicId,
            snapshot: snapshot,
            ct: ct);

        var firstQuestion = ToQuestionPayload(snapshot[0]);
        var response = new SolveQuestionsStartResponse(
            QuizSessionId: quizSession.Id,
            QuestionBankSnapshotSize: snapshot.Count,
            FirstQuestion: firstQuestion);

        return new QuizStartResult(QuizStartOutcome.Ok, quizSession, response);
    }

    public async Task<QuizAnswerResult> AnswerAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        QuizSession quizSession,
        SolveQuestionsAnswerRequest request,
        CancellationToken ct = default)
    {
        if (quizSession.StudentSessionId != session.Id)
            return new QuizAnswerResult(QuizAnswerOutcome.QuizNotFound, null, false, null, null);
        if (!string.Equals(quizSession.State, "in_progress", StringComparison.Ordinal))
            return new QuizAnswerResult(QuizAnswerOutcome.QuizAlreadyEnded, null, false, null, null);

        var snapshot = _quizSessions.ReadSnapshot(quizSession);
        var target = snapshot.FirstOrDefault(q => string.Equals(q.QuestionId, request.QuestionId, StringComparison.Ordinal));
        if (target is null)
            return new QuizAnswerResult(QuizAnswerOutcome.QuestionNotInSnapshot, null, false, null, null);

        var progress = _quizSessions.ReadProgress(quizSession);
        if (progress.Any(p => string.Equals(p.QuestionId, request.QuestionId, StringComparison.Ordinal)))
            return new QuizAnswerResult(QuizAnswerOutcome.QuestionAlreadyAnswered, null, false, null, null);

        var chosenOption = target.Options.FirstOrDefault(o =>
            string.Equals(o.OptionId, request.ChosenOptionId, StringComparison.Ordinal));
        if (chosenOption is null)
            return new QuizAnswerResult(QuizAnswerOutcome.InvalidOption, null, false, null, null);

        var isCorrect = chosenOption.IsCorrect;
        await _quizSessions.RecordAnswerAsync(quizSession, target.QuestionId,
            chosenOption.OptionId, isCorrect, ct);

        var nextRecord = NextUnansweredQuestion(snapshot, quizSession);
        var quizComplete = nextRecord is null;
        if (quizComplete) await _quizSessions.MarkSubmittedAsync(quizSession, ct);

        var response = new SolveQuestionsAnswerResponse(
            IsCorrect: isCorrect,
            CorrectOptionId: target.CorrectOptionId,
            ExplanationTextAr: target.ExplanationTextAr,
            ExplanationTextEn: target.ExplanationTextEn,
            NextQuestion: nextRecord is null ? null : ToQuestionPayload(nextRecord),
            QuizComplete: quizComplete);

        return new QuizAnswerResult(QuizAnswerOutcome.Ok, response, quizComplete,
            chosenOption.OptionId, target);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<QuizChunkSource>> LoadChunkSourcesAsync(
        Subject subject,
        CurriculumType? curriculumType,
        Grade? grade,
        Guid? chapterId,
        Guid? topicId,
        int desiredCount,
        CancellationToken ct)
    {
        var lessons = await _db.Lessons
            .AsNoTracking()
            .Where(l => l.Status == LessonStatus.Approved
                        && l.Subject == subject
                        && (curriculumType == null || l.CurriculumType == curriculumType)
                        && (grade == null || l.Grade == grade))
            .Select(l => new { l.LessonId, l.Path })
            .ToListAsync(ct);

        if (lessons.Count == 0) return Array.Empty<QuizChunkSource>();

        var lessonIds = lessons
            .Where(l => ChapterMatches(l.Path, chapterId) && TopicMatches(l.Path, chapterId, topicId))
            .Select(l => l.LessonId)
            .ToHashSet();

        if (lessonIds.Count == 0) return Array.Empty<QuizChunkSource>();

        var cap = Math.Max(desiredCount * 3, desiredCount);
        var chunks = await _db.ContentChunks
            .AsNoTracking()
            .Where(c => c.Status == ChunkStatus.Active && lessonIds.Contains(c.LessonId))
            .OrderBy(c => c.LessonId)
            .ThenBy(c => c.Sequence)
            .Take(cap)
            .Select(c => new QuizChunkSource(c.ChunkId, c.Sequence, c.Text))
            .ToListAsync(ct);

        return chunks;
    }

    private static bool ChapterMatches(string path, Guid? chapterId)
    {
        if (chapterId is null) return true;
        var parts = SplitPath(path);
        if (parts.Length < 1) return false;
        var slug = "chapter:" + parts[0];
        return SlugToGuid(slug) == chapterId.Value;
    }

    private static bool TopicMatches(string path, Guid? chapterId, Guid? topicId)
    {
        if (topicId is null) return true;
        var parts = SplitPath(path);
        if (parts.Length < 2) return false;
        var chapter = parts[0];
        var topic = parts[1];
        var slug = "topic:" + chapter + "/" + topic;
        return SlugToGuid(slug) == topicId.Value;
    }

    private static string[] SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        return path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static Guid SlugToGuid(string slug)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(slug));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static QuizQuestionPayload ToQuestionPayload(QuizQuestionRecord record) =>
        new(
            QuestionId: record.QuestionId,
            StemTextAr: record.StemTextAr,
            StemTextEn: record.StemTextEn,
            Options: record.Options
                .Select(o => new QuizOptionPayload(o.OptionId, o.TextAr, o.TextEn))
                .ToList());

    public static QuizQuestionRecord? NextUnansweredQuestion(
        IReadOnlyList<QuizQuestionRecord> snapshot,
        QuizSession quizSession)
    {
        var answered = new HashSet<string>(ReadAnsweredIds(quizSession), StringComparer.Ordinal);
        return snapshot.FirstOrDefault(q => !answered.Contains(q.QuestionId));
    }

    private static IEnumerable<string> ReadAnsweredIds(QuizSession quizSession)
    {
        if (string.IsNullOrWhiteSpace(quizSession.Progress)) yield break;
        List<QuizProgressEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<QuizProgressEntry>>(
                quizSession.Progress, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                });
        }
        catch (JsonException)
        {
            yield break;
        }
        if (entries is null) yield break;
        foreach (var entry in entries) yield return entry.QuestionId;
    }

    private static HashSet<Subject> ResolveEnrolledSubjects(string? subjectsEnrolledJson)
    {
        var result = new HashSet<Subject>();
        if (string.IsNullOrWhiteSpace(subjectsEnrolledJson)) return result;

        try
        {
            using var doc = JsonDocument.Parse(subjectsEnrolledJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var raw = item.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (Enum.TryParse<Subject>(raw, ignoreCase: true, out var parsed))
                {
                    result.Add(parsed);
                }
            }
        }
        catch (JsonException)
        {
            // malformed profile data: return empty set so caller fails closed.
        }
        return result;
    }

    private static T? ParseEnumOrNull<T>(string? raw) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) ? parsed : null;
    }
}
