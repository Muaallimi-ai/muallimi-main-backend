namespace Muallimi.Domain.Shared;

public enum CurriculumType
{
    Moe,
    LanguageSchool,
    International
}

public enum Grade
{
    Grade7
}

public enum Subject
{
    Mathematics,
    Science,
    ArabicLanguage,
    EnglishLanguage
}

public enum TutorLanguage
{
    Ar,
    En
}

public enum FileFormat
{
    Pdf,
    Docx,
    Html
}

public enum SourceStatus
{
    Received,
    Ingesting,
    Indexed,
    Failed,
    Replaced
}

public enum LessonStatus
{
    New,
    Ingested,
    Generating,
    AutoValidating,
    PendingAdminReview,
    PendingExpertReview,
    Approved,
    Rejected,
    Invalidated,
    Deprecated
}

public enum ChunkStatus
{
    Draft,
    Active,
    Deprecated
}

public enum AssetType
{
    TextSummary,
    Audio,
    Visual,
    QuizItem,
    QaCacheEntry
}

public enum VisualFormat
{
    Mp4Animation,
    InteractiveHtml,
    Whiteboard,
    Diagram
}

public enum AssetStatus
{
    Queued,
    Producing,
    AutoValidating,
    AutoFailed,
    PendingAdminReview,
    PendingExpertReview,
    Approved,
    Rejected,
    EditRequested,
    Invalidated,
    Superseded
}

public enum ReviewTier
{
    AutoValidation,
    AdminReview,
    ExpertReview
}

public enum ReviewOutcome
{
    Approved,
    Rejected,
    EditRequested
}

public enum ReviewAssignmentStatus
{
    Open,
    InReview,
    Submitted,
    Escalated,
    Expired
}

public enum PublishedAssetStatus
{
    Active,
    Invalidated,
    Replaced
}

public enum QaCacheStatus
{
    PreSeeded,
    PendingReview,
    Active,
    FlaggedForRevalidation,
    Retired
}

public enum CoverageState
{
    Approved,
    PendingReview,
    InProduction,
    Failed,
    NotStarted
}

public enum ChangeEventType
{
    LessonUpdated,
    AssetInvalidated,
    AssetReplaced,
    AssetDeprecated
}

public enum IngestionJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed
}

public enum GenerationJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    PartialFailed
}

public enum AutoValidationDecision
{
    Passed,
    Failed
}
