using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.Engagement.ProgressIngestion;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Engagement.ProgressIngestion;

/// <summary>
/// T028 (US4) — Contract tests for <c>phase4.progress.ingestion</c>.
///
/// Pins the envelope shape that the Phase 3 dispatcher publishes on
/// <c>phase3.session.events</c> and that the Phase 4
/// <see cref="Phase3EventConsumer"/> decodes. The shape must exactly match
/// <c>specs/006-engagement-progress-parent/contracts/progress-ingestion-contract.md</c>.
///
/// Also fixes the eleven Phase 3 event kinds — an additive-only list that
/// consumers MUST ignore unknown kinds from, so Phase 3 can grow a twelfth
/// kind without breaking this contract.
/// </summary>
public class ProgressIngestionContractTests
{
    [Fact]
    public void Phase3EventEnvelope_Carries_Every_Contract_Field()
    {
        var props = typeof(Phase3EventEnvelope)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SourceEventId", props);
        Assert.Contains("EventKind", props);
        Assert.Contains("TenantId", props);
        Assert.Contains("StudentId", props);
        Assert.Contains("CorrelationId", props);
        Assert.Contains("OccurredAt", props);
        Assert.Contains("CurriculumScope", props);
        Assert.Contains("Payload", props);
    }

    [Fact]
    public void Eleven_Phase3_Event_Kinds_Are_Accepted()
    {
        var expected = new[]
        {
            "session_start", "lesson_view", "content_play", "question_asked",
            "answer_received", "refusal", "quiz_answered", "mock_test",
            "homework_help_used", "whiteboard_session", "session_end",
        };
        foreach (var kind in expected)
        {
            Assert.Contains(kind, Phase3EventKinds.All);
        }
        Assert.Equal(expected.Length, Phase3EventKinds.All.Count);
    }

    [Fact]
    public void SessionStart_Kind_Is_Pinned()
    {
        Assert.Equal("session_start", Phase3EventKinds.SessionStart);
        Assert.Equal("session_end", Phase3EventKinds.SessionEnd);
        Assert.Equal("quiz_answered", Phase3EventKinds.QuizAnswered);
        Assert.Equal("mock_test", Phase3EventKinds.MockTest);
        Assert.Equal("homework_help_used", Phase3EventKinds.HomeworkHelpUsed);
        Assert.Equal("whiteboard_session", Phase3EventKinds.WhiteboardSession);
    }

    [Fact]
    public void ProgressIngestionOutcome_Exposes_Insert_Duplicate_Reject()
    {
        var values = Enum.GetNames(typeof(ProgressIngestionOutcome)).ToHashSet();
        Assert.Contains("Inserted", values);
        Assert.Contains("Duplicate", values);
        Assert.Contains("Rejected", values);
    }
}
