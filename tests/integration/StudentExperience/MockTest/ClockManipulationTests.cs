using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.StudentExperience.MockTest;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.MockTest;

/// <summary>
/// T101 (US6) — Clock-manipulation bypass test.
///
/// The contract guarantees that the mock test deadline is computed
/// server-side and that forging a <c>server_now</c> on the client cannot
/// extend the session past <c>server_deadline_at</c>. These tests exercise
/// the service + repository behaviour directly so the check runs without a
/// live HTTP surface, and they fail closed for three bypass attempts:
///
///   1. Submitting an answer after the server deadline is rejected with
///      <see cref="MockTestAnswerOutcome.TimedOut"/>, no matter what the
///      client claims the current time is.
///   2. Calling GetStateAsync after the deadline auto-transitions the
///      session to <c>timed_out</c> using <see cref="DateTime.UtcNow"/>;
///      no client header or payload field can delay that transition.
///   3. Submitting after a timed-out auto-transition returns
///      <see cref="MockTestSubmitOutcome.AlreadyEnded"/> — a second
///      submit call from a forged clock cannot re-open the session.
/// </summary>
public class ClockManipulationTests
{
    private static (MockTestService service, StubMockTestSessionRepository repo, MockTestSession session) Setup(
        IReadOnlyList<MockTestQuestionRecord> snapshot,
        DateTime serverStartedAt,
        DateTime serverDeadlineAt)
    {
        var repo = new StubMockTestSessionRepository();
        var session = new MockTestSession
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000000"),
            StudentSessionId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            SubjectId = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            QuestionBankSnapshot = JsonSerializer.Serialize(snapshot, StubMockTestSessionRepository.JsonOptions),
            TimeLimitSeconds = (int)(serverDeadlineAt - serverStartedAt).TotalSeconds,
            ServerStartedAt = serverStartedAt,
            ServerDeadlineAt = serverDeadlineAt,
            Progress = "[]",
            State = "in_progress",
            PlanTierSnapshot = "free",
        };
        repo.SeedSession(session, snapshot);

        var service = new MockTestService(
            db: null!,
            questionBank: new DeterministicQuizQuestionBank(),
            mockSessions: repo);
        return (service, repo, session);
    }

    private static IReadOnlyList<MockTestQuestionRecord> BuildSnapshot()
    {
        var bank = new DeterministicQuizQuestionBank();
        var sources = Enumerable.Range(0, 3)
            .Select(i => new QuizChunkSource(
                ChunkId: Guid.Parse($"00000000-0000-0000-0000-{i:D12}"),
                Sequence: i,
                Text: $"الفقرة المعتمدة رقم {i}."))
            .ToList();
        var quizRecords = bank.ProjectFromChunks(sources, sources.Count);
        return quizRecords
            .Select((q, idx) => new MockTestQuestionRecord(
                QuestionId: q.QuestionId,
                ChapterId: "chapter-" + idx,
                TopicId: "topic-" + idx,
                StemTextAr: q.StemTextAr,
                StemTextEn: q.StemTextEn,
                Options: q.Options.Select(o => new MockTestOptionRecord(
                    OptionId: o.OptionId,
                    TextAr: o.TextAr,
                    TextEn: o.TextEn,
                    IsCorrect: o.IsCorrect)).ToList(),
                CorrectOptionId: q.CorrectOptionId))
            .ToList();
    }

    [Fact]
    public async Task RecordAnswer_After_Server_Deadline_Is_Rejected_As_TimedOut()
    {
        var snapshot = BuildSnapshot();
        // Start the "session" one hour in the past with a 5-minute limit so
        // the server considers the deadline already expired, regardless of
        // any client header.
        var startedAt = DateTime.UtcNow.AddHours(-1);
        var deadlineAt = startedAt.AddMinutes(5);
        var (service, _, session) = Setup(snapshot, startedAt, deadlineAt);

        var studentSession = new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = session.StudentSessionId,
            TenantId = session.TenantId,
            PlanTierSnapshot = "free",
        };

        var request = new MockTestAnswerRequest(
            QuestionId: snapshot[0].QuestionId,
            ChosenOptionId: snapshot[0].CorrectOptionId,
            IsFlagged: false);

        var result = await service.RecordAnswerAsync(studentSession, session, request);

        Assert.Equal(MockTestAnswerOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task GetState_After_Server_Deadline_Auto_Transitions_To_TimedOut()
    {
        var snapshot = BuildSnapshot();
        var startedAt = DateTime.UtcNow.AddMinutes(-30);
        var deadlineAt = startedAt.AddMinutes(5);
        var (service, repo, session) = Setup(snapshot, startedAt, deadlineAt);
        var studentSession = new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = session.StudentSessionId,
            TenantId = session.TenantId,
            PlanTierSnapshot = "free",
        };

        var result = await service.GetStateAsync(studentSession, session);

        Assert.Equal(MockTestStateOutcome.AutoTimedOut, result.Outcome);
        Assert.Equal(0, result.Response!.SecondsRemaining);
        Assert.Equal("timed_out", result.Response.State);
        Assert.Equal("timed_out", repo.FindSession(session.Id)!.State);
    }

    [Fact]
    public async Task Seconds_Remaining_Is_Computed_From_UtcNow_Not_Client_Supplied_Now()
    {
        var snapshot = BuildSnapshot();
        // Set the deadline 10 minutes in the future from "now" on the
        // server clock. No matter what the client sends as server_now
        // (which is never in the request model anyway), the service uses
        // DateTime.UtcNow.
        var startedAt = DateTime.UtcNow;
        var deadlineAt = startedAt.AddMinutes(10);
        var (service, _, session) = Setup(snapshot, startedAt, deadlineAt);
        var studentSession = new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = session.StudentSessionId,
            TenantId = session.TenantId,
            PlanTierSnapshot = "free",
        };

        var result = await service.GetStateAsync(studentSession, session);

        Assert.Equal(MockTestStateOutcome.Ok, result.Outcome);
        Assert.InRange(result.Response!.SecondsRemaining, 60 * 9 - 5, 60 * 10);
    }

    [Fact]
    public async Task Submit_After_AutoTimeout_Returns_AlreadyEnded_On_Second_Call()
    {
        var snapshot = BuildSnapshot();
        var startedAt = DateTime.UtcNow.AddHours(-2);
        var deadlineAt = startedAt.AddMinutes(5);
        var (service, _, session) = Setup(snapshot, startedAt, deadlineAt);
        var studentSession = new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = session.StudentSessionId,
            TenantId = session.TenantId,
            PlanTierSnapshot = "free",
        };

        var stateResult = await service.GetStateAsync(studentSession, session);
        Assert.Equal(MockTestStateOutcome.AutoTimedOut, stateResult.Outcome);

        // Second call — a forged-clock replay — cannot re-open the session.
        var submit = await service.SubmitAsync(studentSession, session, clientInitiated: true);
        Assert.Equal(MockTestSubmitOutcome.AlreadyEnded, submit.Outcome);
    }

    [Fact]
    public void Mock_Test_Contract_Has_No_Client_Supplied_Server_Now_Field()
    {
        // Defence-in-depth structural check: the request contract for
        // Start/Answer MUST NOT accept a client-supplied "server_now" or
        // "deadline" override. If a future refactor adds one, this test
        // fails until the design intent is revisited.
        var startProps = typeof(MockTestStartRequest)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.DoesNotContain("ServerNow", startProps);
        Assert.DoesNotContain("ServerDeadlineAt", startProps);

        var answerProps = typeof(MockTestAnswerRequest)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.DoesNotContain("ServerNow", answerProps);
        Assert.DoesNotContain("ServerDeadlineAt", answerProps);
    }

    private sealed class StubMockTestSessionRepository : IMockTestSessionRepository
    {
        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private readonly Dictionary<Guid, MockTestSession> _byId = new();

        public void SeedSession(MockTestSession session, IReadOnlyList<MockTestQuestionRecord> snapshot)
        {
            session.QuestionBankSnapshot = JsonSerializer.Serialize(snapshot, JsonOptions);
            _byId[session.Id] = session;
        }

        public MockTestSession? FindSession(Guid id) =>
            _byId.TryGetValue(id, out var session) ? session : null;

        public Task<MockTestSession> CreateAsync(
            Guid tenantId, Guid studentSessionId, Guid subjectId, int timeLimitSeconds,
            string planTierSnapshot, IReadOnlyList<MockTestQuestionRecord> snapshot,
            CancellationToken ct = default)
            => throw new NotSupportedException("Stub does not support CreateAsync.");

        public Task<MockTestSession?> FindAsync(Guid mockTestSessionId, CancellationToken ct = default) =>
            Task.FromResult(FindSession(mockTestSessionId));

        public IReadOnlyList<MockTestQuestionRecord> ReadSnapshot(MockTestSession session)
        {
            if (string.IsNullOrWhiteSpace(session.QuestionBankSnapshot))
                return Array.Empty<MockTestQuestionRecord>();
            var records = JsonSerializer.Deserialize<List<MockTestQuestionRecord>>(
                session.QuestionBankSnapshot, JsonOptions);
            return records ?? new List<MockTestQuestionRecord>();
        }

        public IReadOnlyList<MockTestProgressEntry> ReadProgress(MockTestSession session)
        {
            if (string.IsNullOrWhiteSpace(session.Progress))
                return Array.Empty<MockTestProgressEntry>();
            var entries = JsonSerializer.Deserialize<List<MockTestProgressEntry>>(
                session.Progress, JsonOptions);
            return entries ?? new List<MockTestProgressEntry>();
        }

        public Task RecordAnswerAsync(
            MockTestSession session, string questionId, string? chosenOptionId,
            bool isFlagged, CancellationToken ct = default)
        {
            var entries = ReadProgress(session).ToList();
            var existing = entries.FindIndex(p => p.QuestionId == questionId);
            var entry = new MockTestProgressEntry(
                QuestionId: questionId,
                ChosenOptionId: chosenOptionId,
                IsFlagged: isFlagged,
                AnsweredAt: chosenOptionId is null ? null : DateTime.UtcNow);
            if (existing >= 0) entries[existing] = entry;
            else entries.Add(entry);
            session.Progress = JsonSerializer.Serialize(entries, JsonOptions);
            return Task.CompletedTask;
        }

        public Task<MockTestSession> MarkSubmittedAsync(
            MockTestSession session, bool timedOut, double finalScorePercent,
            CancellationToken ct = default)
        {
            session.State = timedOut ? "timed_out" : "submitted";
            session.FinalScore = finalScorePercent;
            return Task.FromResult(session);
        }

        public Task<MockTestSession> MarkAbandonedAsync(
            MockTestSession session, CancellationToken ct = default)
        {
            session.State = "abandoned";
            return Task.FromResult(session);
        }
    }
}
