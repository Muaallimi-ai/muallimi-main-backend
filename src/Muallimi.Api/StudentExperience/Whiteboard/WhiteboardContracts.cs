using System;
using System.Collections.Generic;

namespace Muallimi.Api.StudentExperience.Whiteboard;

/// <summary>
/// T115–T118 (US8) — Wire DTOs for the Phase 3 Live Whiteboard surface.
///
/// Shapes follow
/// <c>specs/005-student-learning-experience/contracts/whiteboard-session-contract.md</c>
/// and serialise snake_case via the pipeline-wide JSON naming policy.
///
/// The whiteboard is plan-gated and subject-gated; the same response record
/// carries either the accepted fields or the refusal fields, so the endpoint
/// can return a single shape whether the session starts or is refused at the
/// gate. Step-through playback returns primitive draw operations grounded in
/// an approved Phase 1 content chunk plus bilingual narration and an
/// explicit voice-profile source so Phase 1 teacher voices and Phase 2 AI
/// tutor voices never collide on the wire.
/// </summary>
public sealed record WhiteboardStartRequest(
    Guid SessionId,
    Guid SubjectId,
    Guid TopicId,
    string SessionMode);

public sealed record WhiteboardStartResponse(
    Guid? WhiteboardSessionId,
    Guid? SubjectId,
    Guid? TopicId,
    string? PlanTierSnapshot,
    DateTime? StartedAt,
    WhiteboardCanvasState? InitialCanvasState,
    string? RefusalReason,
    string? RefusalTextAr,
    string? RefusalTextEn);

public sealed record WhiteboardCanvasState(
    int Width,
    int Height,
    string BackgroundColor,
    string TextDirection);

public sealed record WhiteboardStepRequest(
    Guid WhiteboardSessionId,
    int RequestedStepIndex);

public sealed record WhiteboardStepResponse(
    Guid WhiteboardSessionId,
    int StepIndex,
    IReadOnlyList<WhiteboardDrawOp> DrawOps,
    string NarrationTextAr,
    string NarrationTextEn,
    string NarrationVoiceProfileId,
    string NarrationVoiceProfileSource,
    IReadOnlyList<WhiteboardEvidenceRef> EvidenceRefs);

public sealed record WhiteboardDrawOp(
    string OpType,
    WhiteboardDrawPayload Payload);

public sealed record WhiteboardDrawPayload(
    string? Text,
    string? Latex,
    string? TextDirection,
    int? X,
    int? Y,
    int? Width,
    int? Height,
    string? Color,
    IReadOnlyList<int>? Path);

public sealed record WhiteboardEvidenceRef(
    string ChunkId,
    string SourceUri);

public sealed record WhiteboardEndRequest(
    Guid WhiteboardSessionId,
    string EndReason);

public sealed record WhiteboardEndResponse(
    Guid WhiteboardSessionId,
    DateTime EndedAt,
    string EndReason,
    int StepsPlayed);

public static class WhiteboardSessionModes
{
    public const string StepThrough = "step_through";
    public const string FreeDrawGated = "free_draw_gated";

    public static bool IsAccepted(string? mode) =>
        mode is StepThrough or FreeDrawGated;
}

public static class WhiteboardEndReasons
{
    public const string StudentEnded = "student_ended";
    public const string Timeout = "timeout";
    public const string GateRevoked = "gate_revoked";

    public static bool IsAccepted(string? reason) =>
        reason is StudentEnded or Timeout or GateRevoked;
}

public static class WhiteboardRefusalReasons
{
    public const string PlanGate = "plan_gate";
    public const string SubjectGate = "subject_gate";
    public const string TenantDenied = "tenant_denied";
    public const string ScopeNotFound = "scope_not_found";
}

public static class WhiteboardDrawOpTypes
{
    public const string DrawStroke = "draw_stroke";
    public const string DrawLatex = "draw_latex";
    public const string Highlight = "highlight";
    public const string Clear = "clear";
}

public static class WhiteboardNarrationVoiceProfileSources
{
    /// <summary>Phase 1 teacher voice id; distinct from Phase 2 AI tutor voice.</summary>
    public const string Phase1Curriculum = "phase1_curriculum";

    /// <summary>Phase 2 AI tutor voice id; never issued from Phase 1 teacher pool.</summary>
    public const string Phase2AiTutor = "phase2_ai_tutor";
}
