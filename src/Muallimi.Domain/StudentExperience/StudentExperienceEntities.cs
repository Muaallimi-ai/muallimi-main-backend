using System;
using Muallimi.Domain.Shared;

namespace Muallimi.Domain.StudentExperience;

public class StudentProfile : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarReference { get; set; }
    public string CurriculumType { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "ar";
    public bool? RtlOverride { get; set; }
    public string PlanTier { get; set; } = "free";
    public string SubjectsEnrolled { get; set; } = "[]";
    public string ConsentState { get; set; } = "pending";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Phase 9 identity FK. Populated by the legacy backfill (US7). Null until linked.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Child date-of-birth (Phase 9 parent-children flow). Nullable for legacy rows.</summary>
    public DateOnly? Birthday { get; set; }
    /// <summary>Child gender: "male", "female", or null (Phase 9 parent-children flow).</summary>
    public string? Gender { get; set; }

    // ── Extended add-child fields ─────────────────────────────────────
    /// <summary>Avatar background color hex (e.g. "#1a3a2a"). Pairs with AvatarReference (emoji).</summary>
    public string? AvatarBgColor { get; set; }
    /// <summary>School name free-text as entered by parent. Informational only.</summary>
    public string? SchoolName { get; set; }
    /// <summary>Parent-assessed level: "beginner", "intermediate", "advanced". Null = skipped.</summary>
    public string? PrefLevel { get; set; }
    /// <summary>JSON array of preferred learning styles e.g. ["videos","exercises"]. Null = skipped.</summary>
    public string? PrefStyles { get; set; }
    /// <summary>Primary goal: "improve_level", "excel", "review_support". Null = skipped.</summary>
    public string? PrefGoal { get; set; }
}

public class StudentSession : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentProfileId { get; set; }
    public Guid CorrelationId { get; set; }
    public string? ActiveCurriculumType { get; set; }
    public string? ActiveGrade { get; set; }
    public Guid? ActiveSubjectId { get; set; }
    public Guid? ActiveChapterId { get; set; }
    public Guid? ActiveTopicId { get; set; }
    public Guid? ActiveLessonId { get; set; }
    public string ActiveMode { get; set; } = "home";
    public string TutorLanguage { get; set; } = "ar";
    public string DeviceClass { get; set; } = "desktop";
    public string PlanTierSnapshot { get; set; } = "free";
    public DateTime SessionStartedAt { get; set; }
    public DateTime SessionLastActivityAt { get; set; }
    public DateTime? SessionEndedAt { get; set; }
    public string? EndReason { get; set; }
}

public class LessonViewerState : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public Guid LessonId { get; set; }
    public string ViewerPosition { get; set; } = "{}";
    public string PlaybackState { get; set; } = "idle";
    public string? TeacherVoiceProfileId { get; set; }
    public bool CaptionsEnabled { get; set; }
    public double Rate { get; set; } = 1.0;
    public DateTime LastInteractionAt { get; set; }
}

public class TutorChatMessage : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public int TurnNumber { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Modality { get; set; } = "text";
    public string Language { get; set; } = "ar";
    public string? ContentText { get; set; }
    public string? VoiceCaptureReference { get; set; }
    public string? VoicePlaybackReference { get; set; }
    public Guid? AiRequestRecordId { get; set; }
    public string? GuardrailFinalStage { get; set; }
    public string? FinalOutcome { get; set; }
    public string? ConfidenceSignal { get; set; }
    public string EvidenceRefs { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}

public class VoiceCapture : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public Guid? TutorChatMessageId { get; set; }
    public string BlobReference { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public string Codec { get; set; } = "opus";
    public string UploadState { get; set; } = "pending";
    public string SttState { get; set; } = "pending";
    public string? TranscriptText { get; set; }
    public Guid? SttAdapterBindingId { get; set; }
    public DateTime RetentionUntil { get; set; }
}

public class QuizSession : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid? ChapterId { get; set; }
    public Guid? TopicId { get; set; }
    public string QuestionBankSnapshot { get; set; } = "[]";
    public string Progress { get; set; } = "[]";
    public string State { get; set; } = "in_progress";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}

public class MockTestSession : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public Guid SubjectId { get; set; }
    public string QuestionBankSnapshot { get; set; } = "[]";
    public int TimeLimitSeconds { get; set; }
    public DateTime ServerStartedAt { get; set; }
    public DateTime ServerDeadlineAt { get; set; }
    public string Progress { get; set; } = "[]";
    public string State { get; set; } = "in_progress";
    public string PlanTierSnapshot { get; set; } = "free";
    public double? FinalScore { get; set; }
}

public class HomeworkHelpSubmission : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public string InputModality { get; set; } = "text";
    public string? TextPayload { get; set; }
    public Guid? VoiceCaptureId { get; set; }
    public string? ImageBlobReference { get; set; }
    public string? ImagePreprocessMetadata { get; set; }
    public Guid? OcrAdapterBindingId { get; set; }
    public string? ExtractedProblemText { get; set; }
    public Guid? AiRequestRecordId { get; set; }
    public string? FinalOutcome { get; set; }
    public DateTime RetentionUntil { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class WhiteboardSession : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid? TopicId { get; set; }
    public string PlanTierSnapshot { get; set; } = "premium";
    public string SessionMode { get; set; } = "step_through";
    public string StepLog { get; set; } = "[]";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? EndReason { get; set; }
}

public class PlanGatePolicy
{
    public Guid Id { get; set; }
    /// <summary>Null means the row is a global default.</summary>
    public Guid? TenantId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string RequiredPlanTiers { get; set; } = "[]";
    public string? SubjectScope { get; set; }
    public string? GradeScope { get; set; }
    public DateTime EnabledAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string PolicySource { get; set; } = "global_default";
}

public class SessionEvent : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentSessionId { get; set; }
    public Guid CorrelationId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string EventPayload { get; set; } = "{}";
    public string CurriculumScope { get; set; } = "{}";
    public string PlanTierSnapshot { get; set; } = "free";
    public DateTime CreatedAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public int DispatchAttempts { get; set; }
    public string DispatchState { get; set; } = "pending";
}
