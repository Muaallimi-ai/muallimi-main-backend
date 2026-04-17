using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.MockTest;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.MockTest;

/// <summary>
/// T093 (US6) — Contract tests for
/// <c>POST /student/mock-test/start</c>,
/// <c>GET /student/mock-test/{id}/state</c>, and
/// <c>POST /student/mock-test/{id}/submit</c>.
///
/// Shapes mirror
/// <c>specs/005-student-learning-experience/contracts/quiz-and-mock-test-contract.md</c>.
/// The catalogue entry lives in
/// <c>src/Muallimi.Api/StudentExperience/Contracts/Phase3ContractCatalogue.cs</c>
/// under <c>student.quiz.mock_test</c>.
/// </summary>
public class MockTestContractTests
{
    [Fact]
    public void MockTestStartRequest_Carries_Session_Subject_Chapter_And_Time_Limit()
    {
        var props = typeof(MockTestStartRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("ChapterIds", props);
        Assert.Contains("TimeLimitSeconds", props);
        Assert.Contains("QuestionCount", props);
    }

    [Fact]
    public void MockTestStartResponse_Carries_Timer_And_First_Question()
    {
        var props = typeof(MockTestStartResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("MockTestSessionId", props);
        Assert.Contains("ServerStartedAt", props);
        Assert.Contains("ServerDeadlineAt", props);
        Assert.Contains("QuestionBankSnapshotSize", props);
        Assert.Contains("FirstQuestion", props);
        Assert.Contains("PlanGate", props);
    }

    [Fact]
    public void MockTest_FirstQuestion_Payload_Is_The_Same_Shape_As_Solve_Questions()
    {
        // The mock-test player reuses the Solve Questions question card, so
        // the wire payload must remain identical (no correct option id).
        var firstQuestion = typeof(MockTestStartResponse)
            .GetProperty("FirstQuestion", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(firstQuestion);
        Assert.Equal(typeof(QuizQuestionPayload), firstQuestion!.PropertyType);
    }

    [Fact]
    public void MockTestStateResponse_Exposes_Server_Truth_Timer_And_Progress()
    {
        var props = typeof(MockTestStateResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("MockTestSessionId", props);
        Assert.Contains("ServerNow", props);
        Assert.Contains("ServerDeadlineAt", props);
        Assert.Contains("SecondsRemaining", props);
        Assert.Contains("Progress", props);
        Assert.Contains("CurrentQuestion", props);
        Assert.Contains("State", props);
    }

    [Fact]
    public void MockTestProgressPayload_Shape_Matches_Contract()
    {
        var props = typeof(MockTestProgressPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("QuestionId", props);
        Assert.Contains("ChosenOptionId", props);
        Assert.Contains("IsFlagged", props);
    }

    [Fact]
    public void MockTestAnswerRequest_Carries_Question_Option_And_Flag()
    {
        var props = typeof(MockTestAnswerRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("QuestionId", props);
        Assert.Contains("ChosenOptionId", props);
        Assert.Contains("IsFlagged", props);
    }

    [Fact]
    public void MockTestSubmitResponse_Carries_State_Score_And_Topic_Breakdown()
    {
        var props = typeof(MockTestSubmitResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("MockTestSessionId", props);
        Assert.Contains("State", props);
        Assert.Contains("FinalScore", props);
        Assert.Contains("PerTopicBreakdown", props);
    }

    [Fact]
    public void MockTestFinalScorePayload_Shape_Matches_Contract()
    {
        var props = typeof(MockTestFinalScorePayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("Correct", props);
        Assert.Contains("Total", props);
        Assert.Contains("Percent", props);
    }

    [Fact]
    public void MockTestTopicBreakdownPayload_Shape_Matches_Contract()
    {
        var props = typeof(MockTestTopicBreakdownPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("TopicId", props);
        Assert.Contains("Correct", props);
        Assert.Contains("Total", props);
    }

    [Fact]
    public void Mock_Test_Event_Kind_Exists_In_SessionEventKind_Enum()
    {
        var kinds = Enum.GetNames<Muallimi.Api.StudentExperience.SessionEvents.SessionEventKind>();
        Assert.Contains("mock_test", kinds);
    }

    [Fact]
    public void Start_And_Submit_Do_Not_Leak_CorrectOptionId_On_Inflight_Question_Wire()
    {
        // QuizQuestionPayload is the reused shape; assert the (public) wire
        // does not expose the answer key mid-run — it only surfaces per-
        // question correctness in the submit response (final score card).
        var props = typeof(QuizQuestionPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.DoesNotContain("CorrectOptionId", props);
        Assert.DoesNotContain("AnswerKey", props);
    }

    [Fact]
    public void Mock_Test_Endpoints_Are_Catalogued()
    {
        var entry = Muallimi.Api.StudentExperience.Contracts.Phase3ContractCatalogue.All
            .Single(c => c.ContractId == "student.quiz.mock_test");
        var paths = entry.Endpoints.Select(e => e.Path).ToList();
        Assert.Contains("/student/mock-test/start", paths);
        Assert.Contains("/student/mock-test/state", paths);
        Assert.Contains("/student/mock-test/submit", paths);
    }
}
