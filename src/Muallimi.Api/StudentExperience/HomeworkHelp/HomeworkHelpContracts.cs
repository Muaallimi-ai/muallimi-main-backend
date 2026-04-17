using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Muallimi.Api.StudentExperience.HomeworkHelp;

/// <summary>
/// T105–T107 (US7) — Wire DTOs for the Phase 3 Homework Help surface.
///
/// Shapes mirror
/// <c>specs/005-student-learning-experience/contracts/homework-help-image-contract.md</c>
/// and serialise snake_case via the pipeline-wide JSON naming policy.
///
/// The submit envelope binds the three input modalities (text, voice, image)
/// into a single record so the endpoint surface stays uniform regardless of
/// which client tab the student used. Image submissions MUST carry
/// <see cref="ImagePreprocessMetadata"/>; the server re-checks the EXIF flag
/// and the client checksum before forwarding to the Phase 2 OCR adapter.
/// </summary>
public sealed record HomeworkHelpSubmitRequest(
    Guid SessionId,
    Guid CorrelationId,
    string InputModality,
    string? TextPayload,
    Guid? VoiceCaptureId,
    Guid SubjectId,
    Guid? TopicId,
    string TutorLanguage,
    HomeworkImagePreprocessMetadata? ImagePreprocessMetadata);

public sealed record HomeworkImagePreprocessMetadata(
    int? OriginalWidth,
    int? OriginalHeight,
    int? CompressedWidth,
    int? CompressedHeight,
    bool ExifStripped,
    IReadOnlyList<HomeworkFaceFlag> FaceFlags,
    string ClientChecksum);

public sealed record HomeworkFaceFlag(
    [property: JsonPropertyName("bounding_box")] IReadOnlyList<int> BoundingBox,
    [property: JsonPropertyName("confidence")] double Confidence);

public sealed record HomeworkHelpSubmitResponse(
    Guid SubmissionId,
    string FinalOutcome,
    string? ExtractedProblemText,
    string? AnswerTextAr,
    string? AnswerTextEn,
    IReadOnlyList<HomeworkStepPayload> StepByStep,
    IReadOnlyList<HomeworkEvidenceRefPayload> EvidenceRefs,
    string ConfidenceSignal,
    string AiRequestRecordId,
    string? RefusalReason,
    string? RefusalTextAr,
    string? RefusalTextEn);

public sealed record HomeworkStepPayload(
    int StepIndex,
    string TextAr,
    string TextEn,
    string? Latex);

public sealed record HomeworkEvidenceRefPayload(
    string ChunkId,
    string SourceUri);

public sealed record HomeworkHelpGetResponse(
    Guid SubmissionId,
    Guid SessionId,
    string InputModality,
    string FinalOutcome,
    string? ExtractedProblemText,
    string? TextPayload,
    Guid? VoiceCaptureId,
    string? ImageBlobReference,
    HomeworkImagePreprocessMetadata? ImagePreprocessMetadata,
    Guid? OcrAdapterBindingId,
    Guid? AiRequestRecordId,
    DateTime RetentionUntil,
    DateTime CreatedAt,
    HomeworkHelpSubmitResponse? Response);

public static class HomeworkHelpModalities
{
    public const string Text = "text";
    public const string Voice = "voice";
    public const string Image = "image";

    public static bool IsAccepted(string? modality) =>
        modality is Text or Voice or Image;
}

public static class HomeworkHelpRefusalReasons
{
    public const string Scope = "scope";
    public const string SafetyPii = "safety_pii";
    public const string Grounding = "grounding";
    public const string OcrUnreadable = "ocr_unreadable";
    public const string PlanGate = "plan_gate";
    public const string DirectSolution = "direct_solution";
}
