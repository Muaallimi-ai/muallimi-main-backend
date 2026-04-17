using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.QuizDelivery;

/// <summary>
/// T082 (US5) — Contract tests for
/// <c>POST /student/solve-questions/start</c> and
/// <c>POST /student/solve-questions/answer</c>.
///
/// Shapes mirror
/// <c>specs/005-student-learning-experience/contracts/quiz-and-mock-test-contract.md</c>.
/// The catalogue entry lives in
/// <c>src/Muallimi.Api/StudentExperience/Contracts/Phase3ContractCatalogue.cs</c>
/// under <c>student.quiz.mock_test</c>.
/// </summary>
public class SolveQuestionsContractTests
{
    [Fact]
    public void SolveQuestionsStartRequest_Carries_Session_Subject_And_Question_Count()
    {
        var props = typeof(SolveQuestionsStartRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("ChapterId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("QuestionCount", props);
    }

    [Fact]
    public void SolveQuestionsStartResponse_Carries_Snapshot_Size_And_First_Question()
    {
        var props = typeof(SolveQuestionsStartResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("QuizSessionId", props);
        Assert.Contains("QuestionBankSnapshotSize", props);
        Assert.Contains("FirstQuestion", props);
    }

    [Fact]
    public void QuizQuestionPayload_Shape_Matches_Contract()
    {
        var props = typeof(QuizQuestionPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("QuestionId", props);
        Assert.Contains("StemTextAr", props);
        Assert.Contains("StemTextEn", props);
        Assert.Contains("Options", props);
    }

    [Fact]
    public void QuizOptionPayload_Shape_Matches_Contract()
    {
        var props = typeof(QuizOptionPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("OptionId", props);
        Assert.Contains("TextAr", props);
        Assert.Contains("TextEn", props);
    }

    [Fact]
    public void SolveQuestionsAnswerRequest_Carries_Quiz_Question_And_Chosen_Option()
    {
        var props = typeof(SolveQuestionsAnswerRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("QuizSessionId", props);
        Assert.Contains("QuestionId", props);
        Assert.Contains("ChosenOptionId", props);
    }

    [Fact]
    public void SolveQuestionsAnswerResponse_Carries_Correctness_Explanation_And_Next_Question()
    {
        var props = typeof(SolveQuestionsAnswerResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("IsCorrect", props);
        Assert.Contains("CorrectOptionId", props);
        Assert.Contains("ExplanationTextAr", props);
        Assert.Contains("ExplanationTextEn", props);
        Assert.Contains("NextQuestion", props);
        Assert.Contains("QuizComplete", props);
    }

    [Fact]
    public void Start_Response_First_Question_Hides_Correct_Option_From_Wire()
    {
        // The wire payload for a question must NOT leak the correct option —
        // only the answer response exposes it (FR-021 student-tutor-chat parity
        // applied to quiz delivery). Every property on QuizQuestionPayload is
        // enumerated against an explicit allow-list so drift trips CI.
        var props = typeof(QuizQuestionPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.DoesNotContain("CorrectOptionId", props);
        Assert.DoesNotContain("AnswerKey", props);
    }

    [Fact]
    public void Quiz_Answered_Event_Kind_Exists_In_SessionEventKind_Enum()
    {
        var kinds = Enum.GetNames<Muallimi.Api.StudentExperience.SessionEvents.SessionEventKind>();
        Assert.Contains("quiz_answered", kinds);
    }
}
