using System;
using Muallimi.Domain.StudentExperience;

namespace Muallimi.Domain.Engagement;

public class ProgressRecord : ITenantScoped
{
    public Guid ProgressRecordId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public string SourceEventId { get; set; } = string.Empty;
    public string EventKind { get; set; } = string.Empty;
    public string CurriculumScope { get; set; } = "{}";
    public string Payload { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime IngestedAt { get; set; }
}

public class MasteryState : ITenantScoped
{
    public Guid MasteryStateId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public string CurriculumType { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public Guid? TopicId { get; set; }
    public decimal MasteryScore { get; set; }
    public string MasteryBand { get; set; } = "introduced";
    public string CalculationVersion { get; set; } = string.Empty;
    public DateTime? SampleWindowStart { get; set; }
    public DateTime? SampleWindowEnd { get; set; }
    public int ContributingRecordCount { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public string LastCorrelationId { get; set; } = string.Empty;
}

public class StreakState : ITenantScoped
{
    public Guid StreakStateId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public int CurrentLength { get; set; }
    public int LongestLength { get; set; }
    public DateTime LastQualifyingDay { get; set; }
    public string FamilyTimezone { get; set; } = "Asia/Dubai";
    public string ResetHistory { get; set; } = "[]";
    public DateTime LastUpdatedAt { get; set; }
}

public class BadgeCriterion
{
    public Guid BadgeCriterionId { get; set; }
    public string BadgeKey { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string DisplayNameAr { get; set; } = string.Empty;
    public string DisplayNameEn { get; set; } = string.Empty;
    public string DescriptionAr { get; set; } = string.Empty;
    public string DescriptionEn { get; set; } = string.Empty;
    public string Threshold { get; set; } = "{}";
    public DateTime? RetiredAt { get; set; }
}

public class BadgeAward : ITenantScoped
{
    public Guid BadgeAwardId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public Guid BadgeCriterionId { get; set; }
    public string BadgeCriterionVersion { get; set; } = string.Empty;
    public DateTime AwardedAt { get; set; }
    public string OriginatingProgressRecordIds { get; set; } = "[]";
    public bool CelebrationShown { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public class FocusArea : ITenantScoped
{
    public Guid FocusAreaId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public string CurriculumType { get; set; } = string.Empty;
    public Guid SubjectId { get; set; }
    public Guid ChapterId { get; set; }
    public Guid TopicId { get; set; }
    public string SignalSummary { get; set; } = "{}";
    public string RationaleAr { get; set; } = string.Empty;
    public string RationaleEn { get; set; } = string.Empty;
    public string SuggestedNextStep { get; set; } = "{}";
    public Guid GuardrailDecisionTrailId { get; set; }
    public DateTime ComputedAt { get; set; }
    public DateTime ValidUntil { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public class WeeklyReport : ITenantScoped
{
    public Guid WeeklyReportId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }
    public DateTime GeneratedAt { get; set; }
    public Guid RunId { get; set; }
    public string MasteryDeltas { get; set; } = "{}";
    public string TopFocusAreas { get; set; } = "[]";
    public string AwardedBadges { get; set; } = "[]";
    public string SummaryAr { get; set; } = string.Empty;
    public string SummaryEn { get; set; } = string.Empty;
    public Guid GuardrailDecisionTrailId { get; set; }
    public string EvidenceRefs { get; set; } = "{}";
    public string? ShareTokenHash { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Status { get; set; } = "generating";
}

public class AtRiskFlag : ITenantScoped
{
    public Guid AtRiskFlagId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public string ThresholdVersion { get; set; } = string.Empty;
    public string TriggeringEvidence { get; set; } = "{}";
    public DateTime RaisedAt { get; set; }
    public DateTime? ClearedAt { get; set; }
    public Guid? LinkedInterventionPromptId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedByParentProfileId { get; set; }
}

public class InterventionPrompt : ITenantScoped
{
    public Guid InterventionPromptId { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public Guid? OriginatingFlagId { get; set; }
    public Guid? OriginatingFocusAreaId { get; set; }
    public string BodyAr { get; set; } = string.Empty;
    public string BodyEn { get; set; } = string.Empty;
    public string NextStep { get; set; } = "{}";
    public Guid GuardrailDecisionTrailId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public class Phase4DownstreamEvent : ITenantScoped
{
    public Guid Phase4DownstreamEventId { get; set; }
    public Guid TenantId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public Guid StudentId { get; set; }
    public string Scope { get; set; } = "{}";
    public string Payload { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string DeliveryState { get; set; } = "queued";
    public int DispatchAttempts { get; set; }
}

public class GuardrailDecisionTrail : ITenantScoped
{
    public Guid GuardrailDecisionTrailId { get; set; }
    public Guid TenantId { get; set; }
    public string ArtefactKind { get; set; } = string.Empty;
    public Guid ArtefactId { get; set; }
    public string PromptKey { get; set; } = string.Empty;
    public string ChainOutput { get; set; } = "{}";
    public string FinalStage { get; set; } = "pass";
    public string Language { get; set; } = "ar";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; }
}

public class ProgressIngestionDeadLetter
{
    public Guid DeadLetterId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? StudentId { get; set; }
    public string SourceEventId { get; set; } = string.Empty;
    public string EventKind { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Envelope { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
}
