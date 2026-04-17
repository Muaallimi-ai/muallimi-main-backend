using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.HomeworkHelp;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.HomeworkHelp;

/// <summary>
/// T103 (US7) — Contract test for <c>POST /student/homework-help/submit</c>.
///
/// Asserts the wire shape against
/// <c>specs/005-student-learning-experience/contracts/homework-help-image-contract.md</c>:
///   - the submit envelope MUST carry session, correlation, modality,
///     subject, language, and (for image) preprocess metadata.
///   - the answered response MUST surface the extracted text, localised
///     answer text, step-by-step list, evidence refs, confidence signal,
///     and the Phase 2 AiRequestRecord id.
///   - the refusal response MUST cite a refusal reason from the contract
///     vocabulary and a localised refusal text.
///   - the route is registered in the Phase 3 contract catalogue under
///     <c>student.homework_help</c>.
/// </summary>
public class SubmitContractTests
{
    [Fact]
    public void HomeworkHelpSubmitRequest_Shape_Matches_Contract()
    {
        var props = typeof(HomeworkHelpSubmitRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("CorrelationId", props);
        Assert.Contains("InputModality", props);
        Assert.Contains("TextPayload", props);
        Assert.Contains("VoiceCaptureId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("TutorLanguage", props);
        Assert.Contains("ImagePreprocessMetadata", props);
    }

    [Fact]
    public void HomeworkImagePreprocessMetadata_Shape_Matches_Contract()
    {
        var props = typeof(HomeworkImagePreprocessMetadata)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("OriginalWidth", props);
        Assert.Contains("OriginalHeight", props);
        Assert.Contains("CompressedWidth", props);
        Assert.Contains("CompressedHeight", props);
        Assert.Contains("ExifStripped", props);
        Assert.Contains("FaceFlags", props);
        Assert.Contains("ClientChecksum", props);
    }

    [Fact]
    public void HomeworkHelpSubmitResponse_Carries_Answered_And_Refusal_Fields()
    {
        var props = typeof(HomeworkHelpSubmitResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SubmissionId", props);
        Assert.Contains("FinalOutcome", props);
        Assert.Contains("ExtractedProblemText", props);
        Assert.Contains("AnswerTextAr", props);
        Assert.Contains("AnswerTextEn", props);
        Assert.Contains("StepByStep", props);
        Assert.Contains("EvidenceRefs", props);
        Assert.Contains("ConfidenceSignal", props);
        Assert.Contains("AiRequestRecordId", props);
        Assert.Contains("RefusalReason", props);
        Assert.Contains("RefusalTextAr", props);
        Assert.Contains("RefusalTextEn", props);
    }

    [Fact]
    public void HomeworkStepPayload_Shape_Matches_Contract()
    {
        var props = typeof(HomeworkStepPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("StepIndex", props);
        Assert.Contains("TextAr", props);
        Assert.Contains("TextEn", props);
        Assert.Contains("Latex", props);
    }

    [Fact]
    public void HomeworkEvidenceRefPayload_Shape_Matches_Contract()
    {
        var props = typeof(HomeworkEvidenceRefPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("ChunkId", props);
        Assert.Contains("SourceUri", props);
    }

    [Fact]
    public void Refusal_Vocabulary_Covers_Contract_Reasons()
    {
        Assert.Equal("scope", HomeworkHelpRefusalReasons.Scope);
        Assert.Equal("safety_pii", HomeworkHelpRefusalReasons.SafetyPii);
        Assert.Equal("grounding", HomeworkHelpRefusalReasons.Grounding);
        Assert.Equal("ocr_unreadable", HomeworkHelpRefusalReasons.OcrUnreadable);
        Assert.Equal("plan_gate", HomeworkHelpRefusalReasons.PlanGate);
        Assert.Equal("direct_solution", HomeworkHelpRefusalReasons.DirectSolution);
    }

    [Fact]
    public void Modalities_Cover_Text_Voice_Image_Only()
    {
        Assert.True(HomeworkHelpModalities.IsAccepted("text"));
        Assert.True(HomeworkHelpModalities.IsAccepted("voice"));
        Assert.True(HomeworkHelpModalities.IsAccepted("image"));
        Assert.False(HomeworkHelpModalities.IsAccepted("video"));
        Assert.False(HomeworkHelpModalities.IsAccepted(""));
        Assert.False(HomeworkHelpModalities.IsAccepted(null));
    }

    [Fact]
    public void Homework_Help_Used_Event_Kind_Exists_In_SessionEventKind_Enum()
    {
        var kinds = Enum.GetNames<Muallimi.Api.StudentExperience.SessionEvents.SessionEventKind>();
        Assert.Contains("homework_help_used", kinds);
    }

    [Fact]
    public void Submit_Route_Is_Registered_On_The_Endpoints_Surface()
    {
        Assert.Equal("/api/student/homework-help/submit", HomeworkHelpEndpoints.SubmitRoute);
    }

    [Fact]
    public void Homework_Help_Endpoints_Are_Catalogued()
    {
        var entry = Muallimi.Api.StudentExperience.Contracts.Phase3ContractCatalogue.All
            .Single(c => c.ContractId == "student.homework_help");
        var paths = entry.Endpoints.Select(e => e.Path).ToList();
        Assert.Contains("/student/homework-help/submit", paths);
    }
}
