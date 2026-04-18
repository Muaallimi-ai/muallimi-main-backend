using System;
using Muallimi.Domain.StudentExperience;

namespace Muallimi.Domain.SchoolManagement;

/// <summary>
/// Phase 5 — School Management and B2B Administration entities. See
/// specs/007-school-management-b2b/data-model.md.
/// </summary>
public class SchoolTenant : ITenantScoped
{
    public Guid SchoolTenantId { get; set; }
    public Guid TenantId { get; set; }
    public string SchoolNameAr { get; set; } = string.Empty;
    public string SchoolNameEn { get; set; } = string.Empty;
    public string CurriculumType { get; set; } = "moe";
    public int GradeRangeStart { get; set; }
    public int GradeRangeEnd { get; set; }
    public string SubjectBindings { get; set; } = "[]";
    public string AcademicCalendar { get; set; } = "{}";
    public string PreferredLanguage { get; set; } = "ar";
    public string SubscriptionStatus { get; set; } = "trial";
    public Guid CreatedByOperatorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SchoolAdministrator : ITenantScoped
{
    public Guid SchoolAdminId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public Guid UserIdentityId { get; set; }
    public string InvitationEmail { get; set; } = string.Empty;
    public string OnboardingStatus { get; set; } = "invited";
    public DateTime? TermsAcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
}

public class Teacher : ITenantScoped
{
    public Guid TeacherId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public Guid UserIdentityId { get; set; }
    public string DisplayNameAr { get; set; } = string.Empty;
    public string DisplayNameEn { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
}

public class ClassGroup : ITenantScoped
{
    public Guid ClassGroupId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public int Grade { get; set; }
    public string SectionLabel { get; set; } = string.Empty;
    public string DisplayNameAr { get; set; } = string.Empty;
    public string DisplayNameEn { get; set; } = string.Empty;
    public string SubjectBindings { get; set; } = "[]";
    public string AcademicYear { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ClassEnrolment : ITenantScoped
{
    public Guid ClassEnrolmentId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ClassGroupId { get; set; }
    public Guid StudentId { get; set; }
    public DateTime EnrolledAt { get; set; }
    public DateTime? UnenrolledAt { get; set; }
    public Guid? TransferToClassId { get; set; }
    public string Status { get; set; } = "active";
}

public class TeacherAssignment : ITenantScoped
{
    public Guid TeacherAssignmentId { get; set; }
    public Guid TenantId { get; set; }
    public Guid TeacherId { get; set; }
    public Guid ClassGroupId { get; set; }
    public Guid SubjectId { get; set; }
    public DateTime AssignedAt { get; set; }
    public DateTime? UnassignedAt { get; set; }
}

public class RosterImport : ITenantScoped
{
    public Guid RosterImportId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public Guid UploadedByAdminId { get; set; }
    public string SourceFileBlobKey { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public int TotalRowCount { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public int SkipCount { get; set; }
    public string? ErrorReportBlobKey { get; set; }
    public string Status { get; set; } = "uploaded";
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Exam : ITenantScoped
{
    public Guid ExamId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public Guid? CreatedByTeacherId { get; set; }
    public Guid? CreatedByAdminId { get; set; }
    public string TitleAr { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public int Grade { get; set; }
    public string? TopicBindings { get; set; }
    public DateTime? ScheduledStart { get; set; }
    public DateTime? ScheduledEnd { get; set; }
    public int? DurationMinutes { get; set; }
    public string Status { get; set; } = "draft";
    public decimal TotalPoints { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ExamQuestion : ITenantScoped
{
    public Guid ExamQuestionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ExamId { get; set; }
    public string QuestionSource { get; set; } = "phase1_content";
    public Guid? Phase1ContentId { get; set; }
    public string QuestionTextAr { get; set; } = string.Empty;
    public string QuestionTextEn { get; set; } = string.Empty;
    public string QuestionType { get; set; } = "multiple_choice";
    public string? Options { get; set; }
    public string CorrectAnswer { get; set; } = "{}";
    public decimal Points { get; set; } = 1m;
    public int DisplayOrder { get; set; }
    public Guid? GuardrailDecisionTrailId { get; set; }
}

public class ExamAssignment : ITenantScoped
{
    public Guid ExamAssignmentId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ExamId { get; set; }
    public Guid ClassGroupId { get; set; }
    public DateTime AssignedAt { get; set; }
}

public class ExamSubmission : ITenantScoped
{
    public Guid ExamSubmissionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ExamId { get; set; }
    public Guid StudentId { get; set; }
    public string Answers { get; set; } = "{}";
    public decimal? Score { get; set; }
    public decimal MaxScore { get; set; }
    public string GradingStatus { get; set; } = "submitted";
    public DateTime StartedAt { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? GradedAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public class LeaderboardSnapshot : ITenantScoped
{
    public Guid LeaderboardSnapshotId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public string ScopeType { get; set; } = "class";
    public Guid ScopeId { get; set; }
    public Guid? SubjectId { get; set; }
    public string Metric { get; set; } = "mastery";
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public string Entries { get; set; } = "[]";
    public string PrivacyMode { get; set; } = "first_name_only";
    public DateTime ComputedAt { get; set; }
}

public class LeaderboardConfig : ITenantScoped
{
    public Guid LeaderboardConfigId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public string PrivacyMode { get; set; } = "first_name_only";
    public bool LeaderboardEnabled { get; set; } = true;
    public string PerClassOverridesJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; }
}

public class Announcement : ITenantScoped
{
    public Guid AnnouncementId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public Guid CreatedById { get; set; }
    public string TargetScope { get; set; } = "school";
    public Guid? TargetId { get; set; }
    public int? TargetGrade { get; set; }
    public string TitleAr { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public string BodyAr { get; set; } = string.Empty;
    public string BodyEn { get; set; } = string.Empty;
    public string? Attachments { get; set; }
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; }
}

public class AnnouncementDelivery : ITenantScoped
{
    public Guid AnnouncementDeliveryId { get; set; }
    public Guid TenantId { get; set; }
    public Guid AnnouncementId { get; set; }
    public Guid RecipientId { get; set; }
    public string RecipientRole { get; set; } = "student";
    public string Channel { get; set; } = "in_app";
    public string DeliveryStatus { get; set; } = "queued";
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public class SchoolReport : ITenantScoped
{
    public Guid SchoolReportId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public Guid GeneratedByAdminId { get; set; }
    public string ReportType { get; set; } = "mastery_trends";
    public int? GradeFilter { get; set; }
    public Guid? SubjectFilter { get; set; }
    public Guid? ClassFilter { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public string Language { get; set; } = "ar";
    public string? ExportBlobKey { get; set; }
    public string Status { get; set; } = "generating";
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class SchoolLicense : ITenantScoped
{
    public Guid SchoolLicenseId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public string PlanTier { get; set; } = "trial";
    public int SeatLimit { get; set; }
    public int SeatsUsed { get; set; }
    public string FeatureGates { get; set; } = "{}";
    public DateTime SubscriptionStart { get; set; }
    public DateTime SubscriptionEnd { get; set; }
    public bool IsTrial { get; set; }
    public int SeatWarningThreshold { get; set; } = 90;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SchoolAggregateView : ITenantScoped
{
    public Guid AggregateViewId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public string ScopeType { get; set; } = "school";
    public Guid ScopeId { get; set; }
    public int? Grade { get; set; }
    public Guid? SubjectId { get; set; }
    public int ActiveStudentCount { get; set; }
    public decimal AverageMastery { get; set; }
    public int AtRiskCount { get; set; }
    public int ActiveStreakCount { get; set; }
    public int BadgesAwardedCount { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public Guid LastEventId { get; set; }
}

public class Phase5DownstreamEvent : ITenantScoped
{
    public Guid Phase5EventId { get; set; }
    public Guid TenantId { get; set; }
    public Guid SchoolTenantId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string SchemaVersion { get; set; } = "1.0.0";
    public string DeliveryState { get; set; } = "queued";
    public int DispatchAttempts { get; set; }
}
