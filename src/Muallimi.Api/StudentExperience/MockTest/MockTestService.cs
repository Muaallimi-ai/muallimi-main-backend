using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.MockTest;

/// <summary>
/// T094 (US6) — MockTestService.
///
/// Orchestrates the timed Mock Test facade:
///   - Validates the subject + optional chapter ids against the Phase 1
///     lesson path projection shared with Solve Questions.
///   - Reads approved <see cref="ContentChunk"/> rows filtered by tenant
///     (via the EF global filter), curriculum type, grade, subject, and the
///     requested chapters.
///   - Assembles a proportional question set: questions are distributed
///     across the selected chapters proportional to how many approved
///     chunks exist in each one, then projected into quiz records via
///     <see cref="IQuizQuestionBank"/>.
///   - Persists the snapshot + <c>server_started_at</c> / <c>server_deadline_at</c>
///     through <see cref="IMockTestSessionRepository"/>.
///   - Answer recording, timeout detection, and submission mutate the
///     persisted row; <c>server_now</c> is always
///     <see cref="DateTime.UtcNow"/> (no client-supplied clock).
///
/// Constitution rules respected:
///   - All content sourced from Phase 1 approved tables; no generation here.
///   - Tenant + scope filters applied at query time and re-applied at
///     <c>ReadSnapshot</c> to defend against a mis-scoped retrieval.
///   - Timer truth is server-side only: the client cannot extend a run by
///     forging <c>server_now</c> (see ClockManipulationTests).
///   - Mock test answers emit <c>mock_test</c> session events, never
///     <c>quiz_answered</c> (see MockTestLabelTests).
/// </summary>
public interface IMockTestService
{
    Task<MockTestStartResult> StartAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        MockTestStartRequest request,
        CancellationToken ct = default);

    Task<MockTestAnswerResult> RecordAnswerAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        MockTestSession mockSession,
        MockTestAnswerRequest request,
        CancellationToken ct = default);

    Task<MockTestStateResult> GetStateAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        MockTestSession mockSession,
        CancellationToken ct = default);

    Task<MockTestSubmitResult> SubmitAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        MockTestSession mockSession,
        bool clientInitiated,
        CancellationToken ct = default);
}

public sealed record MockTestStartResult(
    MockTestStartOutcome Outcome,
    MockTestSession? MockSession,
    MockTestStartResponse? Response);

public enum MockTestStartOutcome
{
    Ok,
    InvalidSubject,
    InvalidTimeLimit,
    InvalidQuestionCount,
    PlanGated,
    NoQuestionsAvailable,
}

public sealed record MockTestAnswerResult(
    MockTestAnswerOutcome Outcome,
    bool IsCorrect,
    string? CorrectOptionId);

public enum MockTestAnswerOutcome
{
    Ok,
    AlreadyEnded,
    TimedOut,
    QuestionNotInSnapshot,
    InvalidOption,
}

public sealed record MockTestStateResult(
    MockTestStateOutcome Outcome,
    MockTestStateResponse? Response);

public enum MockTestStateOutcome
{
    Ok,
    AutoTimedOut,
}

public sealed record MockTestSubmitResult(
    MockTestSubmitOutcome Outcome,
    bool TimedOut,
    MockTestSubmitResponse? Response);

public enum MockTestSubmitOutcome
{
    Ok,
    AlreadyEnded,
}

public sealed class MockTestService : IMockTestService
{
    public const int MinTimeLimitSeconds = 60;
    public const int MaxTimeLimitSeconds = 2 * 60 * 60; // 2 hours
    public const int DefaultQuestionCount = 20;
    public const int MinQuestionCount = 5;
    public const int MaxQuestionCount = 50;

    private readonly MuallimiDbContext _db;
    private readonly IQuizQuestionBank _questionBank;
    private readonly IMockTestSessionRepository _mockSessions;

    public MockTestService(
        MuallimiDbContext db,
        IQuizQuestionBank questionBank,
        IMockTestSessionRepository mockSessions)
    {
        _db = db;
        _questionBank = questionBank;
        _mockSessions = mockSessions;
    }

    public async Task<MockTestStartResult> StartAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        StudentProfile profile,
        MockTestStartRequest request,
        CancellationToken ct = default)
    {
        var subject = LessonRetrievalService.SubjectFromGuid(request.SubjectId);
        if (subject is null)
            return new MockTestStartResult(MockTestStartOutcome.InvalidSubject, null, null);

        var enrolled = ResolveEnrolledSubjects(profile.SubjectsEnrolled);
        if (enrolled.Count > 0 && !enrolled.Contains(subject.Value))
            return new MockTestStartResult(MockTestStartOutcome.InvalidSubject, null, null);

        if (request.TimeLimitSeconds < MinTimeLimitSeconds || request.TimeLimitSeconds > MaxTimeLimitSeconds)
            return new MockTestStartResult(MockTestStartOutcome.InvalidTimeLimit, null, null);

        var questionCount = request.QuestionCount ?? DefaultQuestionCount;
        if (questionCount < MinQuestionCount || questionCount > MaxQuestionCount)
            return new MockTestStartResult(MockTestStartOutcome.InvalidQuestionCount, null, null);

        var curriculumType = ParseEnumOrNull<CurriculumType>(profile.CurriculumType);
        var grade = ParseEnumOrNull<Grade>(profile.Grade);
        var chapterFilter = (request.ChapterIds ?? Array.Empty<Guid>()).ToHashSet();

        var loaded = await LoadChunksByChapterAsync(
            subject.Value, curriculumType, grade, chapterFilter, ct);

        if (loaded.Buckets.Count == 0 || loaded.Buckets.Values.All(b => b.Count == 0))
            return new MockTestStartResult(MockTestStartOutcome.NoQuestionsAvailable, null, null);

        var assembled = AssembleProportional(loaded.Buckets, loaded.TopicByChunk, questionCount);
        if (assembled.Count == 0)
            return new MockTestStartResult(MockTestStartOutcome.NoQuestionsAvailable, null, null);

        var mockSession = await _mockSessions.CreateAsync(
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            subjectId: request.SubjectId,
            timeLimitSeconds: request.TimeLimitSeconds,
            planTierSnapshot: session.PlanTierSnapshot,
            snapshot: assembled,
            ct: ct);

        var firstPayload = ToQuestionPayload(assembled[0]);
        var response = new MockTestStartResponse(
            MockTestSessionId: mockSession.Id,
            ServerStartedAt: mockSession.ServerStartedAt,
            ServerDeadlineAt: mockSession.ServerDeadlineAt,
            QuestionBankSnapshotSize: assembled.Count,
            FirstQuestion: firstPayload,
            PlanGate: "open");

        return new MockTestStartResult(MockTestStartOutcome.Ok, mockSession, response);
    }

    public async Task<MockTestAnswerResult> RecordAnswerAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        MockTestSession mockSession,
        MockTestAnswerRequest request,
        CancellationToken ct = default)
    {
        if (!string.Equals(mockSession.State, "in_progress", StringComparison.Ordinal))
            return new MockTestAnswerResult(MockTestAnswerOutcome.AlreadyEnded, false, null);

        if (DateTime.UtcNow >= mockSession.ServerDeadlineAt)
            return new MockTestAnswerResult(MockTestAnswerOutcome.TimedOut, false, null);

        var snapshot = _mockSessions.ReadSnapshot(mockSession);
        var target = snapshot.FirstOrDefault(q =>
            string.Equals(q.QuestionId, request.QuestionId, StringComparison.Ordinal));
        if (target is null)
            return new MockTestAnswerResult(MockTestAnswerOutcome.QuestionNotInSnapshot, false, null);

        string? chosenOptionId = null;
        var isCorrect = false;
        if (!string.IsNullOrWhiteSpace(request.ChosenOptionId))
        {
            var option = target.Options.FirstOrDefault(o =>
                string.Equals(o.OptionId, request.ChosenOptionId, StringComparison.Ordinal));
            if (option is null)
                return new MockTestAnswerResult(MockTestAnswerOutcome.InvalidOption, false, null);
            chosenOptionId = option.OptionId;
            isCorrect = option.IsCorrect;
        }

        await _mockSessions.RecordAnswerAsync(
            mockSession,
            target.QuestionId,
            chosenOptionId,
            request.IsFlagged,
            ct);

        return new MockTestAnswerResult(MockTestAnswerOutcome.Ok, isCorrect, target.CorrectOptionId);
    }

    public async Task<MockTestStateResult> GetStateAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        MockTestSession mockSession,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var outcome = MockTestStateOutcome.Ok;

        if (string.Equals(mockSession.State, "in_progress", StringComparison.Ordinal)
            && now >= mockSession.ServerDeadlineAt)
        {
            var autoScore = ComputeScorePercent(mockSession);
            mockSession = await _mockSessions.MarkSubmittedAsync(
                mockSession, timedOut: true, finalScorePercent: autoScore, ct);
            outcome = MockTestStateOutcome.AutoTimedOut;
            now = DateTime.UtcNow;
        }

        var secondsRemaining = Math.Max(
            0,
            (int)Math.Floor((mockSession.ServerDeadlineAt - now).TotalSeconds));

        var snapshot = _mockSessions.ReadSnapshot(mockSession);
        var progress = _mockSessions.ReadProgress(mockSession);
        var current = NextUnansweredQuestion(snapshot, progress);

        var response = new MockTestStateResponse(
            MockTestSessionId: mockSession.Id,
            ServerNow: now,
            ServerDeadlineAt: mockSession.ServerDeadlineAt,
            SecondsRemaining: secondsRemaining,
            Progress: progress
                .Select(p => new MockTestProgressPayload(
                    QuestionId: p.QuestionId,
                    ChosenOptionId: p.ChosenOptionId,
                    IsFlagged: p.IsFlagged))
                .ToList(),
            CurrentQuestion: current is null ? null : ToQuestionPayload(current),
            State: mockSession.State);

        return new MockTestStateResult(outcome, response);
    }

    public async Task<MockTestSubmitResult> SubmitAsync(
        Muallimi.Domain.StudentExperience.StudentSession session,
        MockTestSession mockSession,
        bool clientInitiated,
        CancellationToken ct = default)
    {
        if (!string.Equals(mockSession.State, "in_progress", StringComparison.Ordinal))
            return new MockTestSubmitResult(MockTestSubmitOutcome.AlreadyEnded, false, null);

        var timedOut = !clientInitiated || DateTime.UtcNow >= mockSession.ServerDeadlineAt;
        var percent = ComputeScorePercent(mockSession);
        mockSession = await _mockSessions.MarkSubmittedAsync(
            mockSession, timedOut: timedOut, finalScorePercent: percent, ct);

        var snapshot = _mockSessions.ReadSnapshot(mockSession);
        var progress = _mockSessions.ReadProgress(mockSession);
        var scored = ScoreSnapshot(snapshot, progress);

        var response = new MockTestSubmitResponse(
            MockTestSessionId: mockSession.Id,
            State: mockSession.State,
            FinalScore: new MockTestFinalScorePayload(
                Correct: scored.Correct,
                Total: scored.Total,
                Percent: Math.Round(percent, 2)),
            PerTopicBreakdown: scored.PerTopic);

        return new MockTestSubmitResult(MockTestSubmitOutcome.Ok, timedOut, response);
    }

    // ── helpers: Phase 1 retrieval ─────────────────────────────────────

    private sealed record ChapterChunkLoad(
        Dictionary<string, List<QuizChunkSource>> Buckets,
        Dictionary<Guid, string> TopicByChunk);

    private async Task<ChapterChunkLoad> LoadChunksByChapterAsync(
        Subject subject,
        CurriculumType? curriculumType,
        Grade? grade,
        HashSet<Guid> chapterFilter,
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

        if (lessons.Count == 0)
            return new ChapterChunkLoad(new(), new());

        var selected = lessons
            .Select(l => new { l.LessonId, l.Path, ChapterSlug = ChapterSlug(l.Path) })
            .Where(l => !string.IsNullOrEmpty(l.ChapterSlug))
            .Where(l => chapterFilter.Count == 0
                        || chapterFilter.Contains(SlugToGuid("chapter:" + l.ChapterSlug)))
            .ToList();

        if (selected.Count == 0)
            return new ChapterChunkLoad(new(), new());

        var lessonIds = selected.Select(l => l.LessonId).ToHashSet();
        var chunks = await _db.ContentChunks
            .AsNoTracking()
            .Where(c => c.Status == ChunkStatus.Active && lessonIds.Contains(c.LessonId))
            .OrderBy(c => c.LessonId)
            .ThenBy(c => c.Sequence)
            .Select(c => new
            {
                c.ChunkId,
                c.Sequence,
                c.Text,
                c.LessonId,
            })
            .ToListAsync(ct);

        var lessonChapter = selected.ToDictionary(l => l.LessonId, l => l.ChapterSlug);
        var lessonTopic = selected.ToDictionary(l => l.LessonId, l => TopicSlug(l.Path));

        var buckets = new Dictionary<string, List<QuizChunkSource>>(StringComparer.Ordinal);
        var topicByChunk = new Dictionary<Guid, string>();
        foreach (var chunk in chunks)
        {
            if (!lessonChapter.TryGetValue(chunk.LessonId, out var chapter)) continue;
            var topic = lessonTopic[chunk.LessonId];
            if (!buckets.TryGetValue(chapter, out var list))
            {
                list = new List<QuizChunkSource>();
                buckets[chapter] = list;
            }
            list.Add(new QuizChunkSource(chunk.ChunkId, chunk.Sequence, chunk.Text));
            topicByChunk[chunk.ChunkId] = topic;
        }
        return new ChapterChunkLoad(buckets, topicByChunk);
    }

    /// <summary>
    /// Distribute <paramref name="questionCount"/> across chapter buckets
    /// proportional to chunk availability (largest-remainder rounding) so
    /// no chapter dominates the mock test.
    /// </summary>
    internal static IReadOnlyList<MockTestQuestionRecord> AssembleProportional(
        IReadOnlyDictionary<string, List<QuizChunkSource>> buckets,
        IReadOnlyDictionary<Guid, string> topicByChunk,
        int questionCount)
    {
        var nonEmpty = buckets
            .Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();
        if (nonEmpty.Count == 0) return Array.Empty<MockTestQuestionRecord>();

        var totalChunks = nonEmpty.Sum(kv => kv.Value.Count);
        var effective = Math.Min(questionCount, totalChunks);
        if (effective <= 0) return Array.Empty<MockTestQuestionRecord>();

        // Compute fair shares via largest-remainder: start with floors,
        // distribute the leftover seats to buckets with the largest
        // fractional remainders. Every non-empty bucket gets at least one
        // seat so chapter coverage is preserved when headroom allows.
        var shares = nonEmpty
            .Select(kv => new
            {
                kv.Key,
                kv.Value,
                Raw = effective * (double)kv.Value.Count / totalChunks,
            })
            .Select(s => new
            {
                s.Key,
                s.Value,
                s.Raw,
                Floor = (int)Math.Floor(s.Raw),
                Fraction = s.Raw - Math.Floor(s.Raw),
            })
            .ToList();

        var assigned = shares.Select(s => Math.Max(s.Floor, 0)).ToList();
        for (var i = 0; i < shares.Count; i++)
        {
            if (shares[i].Value.Count > 0 && assigned[i] == 0 && assigned.Sum() < effective)
                assigned[i] = 1;
        }
        while (assigned.Sum() > effective)
        {
            var idx = assigned
                .Select((v, i) => (Value: v, Index: i))
                .Where(t => t.Value > 1)
                .OrderBy(t => shares[t.Index].Fraction)
                .Select(t => t.Index)
                .FirstOrDefault();
            assigned[idx]--;
        }
        var remaining = effective - assigned.Sum();
        var ordered = shares
            .Select((s, i) => (Share: s, Assigned: assigned[i], Index: i))
            .OrderByDescending(t => t.Share.Fraction)
            .ThenBy(t => t.Share.Key, StringComparer.Ordinal)
            .ToList();
        var cursor = 0;
        while (remaining > 0)
        {
            var slot = ordered[cursor % ordered.Count];
            if (slot.Assigned < slot.Share.Value.Count)
            {
                assigned[slot.Index]++;
                remaining--;
            }
            cursor++;
            if (cursor > ordered.Count * 4) break;
        }

        var records = new List<MockTestQuestionRecord>(effective);
        for (var i = 0; i < shares.Count; i++)
        {
            var take = Math.Min(assigned[i], shares[i].Value.Count);
            foreach (var source in shares[i].Value.Take(take))
            {
                var quiz = DeterministicQuizQuestionBank.BuildRecord(source);
                records.Add(new MockTestQuestionRecord(
                    QuestionId: quiz.QuestionId,
                    ChapterId: SlugToGuid("chapter:" + shares[i].Key).ToString(),
                    TopicId: topicByChunk.TryGetValue(source.ChunkId, out var topic) && !string.IsNullOrEmpty(topic)
                        ? SlugToGuid("topic:" + shares[i].Key + "/" + topic).ToString()
                        : SlugToGuid("topic:" + shares[i].Key + "/default").ToString(),
                    StemTextAr: quiz.StemTextAr,
                    StemTextEn: quiz.StemTextEn,
                    Options: quiz.Options.Select(o => new MockTestOptionRecord(
                        OptionId: o.OptionId,
                        TextAr: o.TextAr,
                        TextEn: o.TextEn,
                        IsCorrect: o.IsCorrect)).ToList(),
                    CorrectOptionId: quiz.CorrectOptionId));
            }
        }
        return records;
    }

    // ── helpers: scoring / projection ─────────────────────────────────

    public static QuizQuestionPayload ToQuestionPayload(MockTestQuestionRecord record) =>
        new(
            QuestionId: record.QuestionId,
            StemTextAr: record.StemTextAr,
            StemTextEn: record.StemTextEn,
            Options: record.Options
                .Select(o => new QuizOptionPayload(o.OptionId, o.TextAr, o.TextEn))
                .ToList());

    public static MockTestQuestionRecord? NextUnansweredQuestion(
        IReadOnlyList<MockTestQuestionRecord> snapshot,
        IReadOnlyList<MockTestProgressEntry> progress)
    {
        var answered = new HashSet<string>(
            progress.Where(p => p.ChosenOptionId is not null).Select(p => p.QuestionId),
            StringComparer.Ordinal);
        return snapshot.FirstOrDefault(q => !answered.Contains(q.QuestionId));
    }

    internal double ComputeScorePercent(MockTestSession session)
    {
        var snapshot = _mockSessions.ReadSnapshot(session);
        var progress = _mockSessions.ReadProgress(session);
        if (snapshot.Count == 0) return 0d;
        var correct = CountCorrect(snapshot, progress);
        return 100d * correct / snapshot.Count;
    }

    public static int CountCorrect(
        IReadOnlyList<MockTestQuestionRecord> snapshot,
        IReadOnlyList<MockTestProgressEntry> progress)
    {
        var byId = progress
            .Where(p => p.ChosenOptionId is not null)
            .ToDictionary(p => p.QuestionId, p => p.ChosenOptionId!, StringComparer.Ordinal);
        var correct = 0;
        foreach (var record in snapshot)
        {
            if (byId.TryGetValue(record.QuestionId, out var chosen)
                && string.Equals(chosen, record.CorrectOptionId, StringComparison.Ordinal))
            {
                correct++;
            }
        }
        return correct;
    }

    public static (int Correct, int Total, IReadOnlyList<MockTestTopicBreakdownPayload> PerTopic) ScoreSnapshot(
        IReadOnlyList<MockTestQuestionRecord> snapshot,
        IReadOnlyList<MockTestProgressEntry> progress)
    {
        var byId = progress
            .Where(p => p.ChosenOptionId is not null)
            .ToDictionary(p => p.QuestionId, p => p.ChosenOptionId!, StringComparer.Ordinal);

        var total = snapshot.Count;
        var correct = 0;
        var perTopic = new Dictionary<string, (int Correct, int Total)>(StringComparer.Ordinal);
        foreach (var record in snapshot)
        {
            var topic = record.TopicId;
            perTopic.TryGetValue(topic, out var tally);
            var isCorrect = byId.TryGetValue(record.QuestionId, out var chosen)
                            && string.Equals(chosen, record.CorrectOptionId, StringComparison.Ordinal);
            perTopic[topic] = (tally.Correct + (isCorrect ? 1 : 0), tally.Total + 1);
            if (isCorrect) correct++;
        }

        var breakdown = perTopic
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new MockTestTopicBreakdownPayload(kv.Key, kv.Value.Correct, kv.Value.Total))
            .ToList();

        return (correct, total, breakdown);
    }

    // ── helpers: paths / slugs ─────────────────────────────────────────

    private static string[] SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        return path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string ChapterSlug(string path)
    {
        var parts = SplitPath(path);
        return parts.Length >= 1 ? parts[0] : string.Empty;
    }

    private static string TopicSlug(string path)
    {
        var parts = SplitPath(path);
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    public static Guid SlugToGuid(string slug)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(slug));
        return new Guid(bytes.AsSpan(0, 16));
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
