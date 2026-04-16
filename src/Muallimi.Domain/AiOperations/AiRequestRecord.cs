namespace Muallimi.Domain.AiOperations;

public class AiRequestRecord
{
    public Guid RecordId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public Guid TenantId { get; set; }
    public string CurriculumType { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string TutorLanguage { get; set; } = string.Empty;
    public string SessionMode { get; set; } = string.Empty;
    public string Stages { get; set; } = "[]";
    public string RoutingDecision { get; set; } = "{}";
    public int InputTokenCount { get; set; }
    public int OutputTokenCount { get; set; }
    public int LatencyMs { get; set; }
    public double? CacheMatchScore { get; set; }
    public string FinalOutcome { get; set; } = string.Empty;
    public string? QuestionTextPreview { get; set; }
    public string PromptVersionsUsed { get; set; } = "[]";
    public DateTime OccurredAt { get; set; }
}

public class RefusalEvent
{
    public Guid EventId { get; set; }
    public Guid RecordId { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string LocalisedReason { get; set; } = string.Empty;
    public string TutorLanguage { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
}

public class AiOperationsMetric
{
    public Guid MetricId { get; set; }
    public string WindowStart { get; set; } = string.Empty;
    public string WindowEnd { get; set; } = string.Empty;
    public string CurriculumType { get; set; } = string.Empty;
    public string Grade { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string TutorLanguage { get; set; } = string.Empty;
    public string SessionMode { get; set; } = string.Empty;
    public long Volume { get; set; }
    public double RefusalRate { get; set; }
    public double CacheHitRate { get; set; }
    public double GroundedAnswerRate { get; set; }
    public string PerBranch { get; set; } = "{}";
    public string PromptVersionDistribution { get; set; } = "{}";
    public DateTime ComputedAt { get; set; }
}

public class RedTeamScenarioSet
{
    public Guid SetId { get; set; }
    public string Version { get; set; } = string.Empty;
    public string StorageKey { get; set; } = string.Empty;
    public int ScenarioCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RedTeamEvaluationResult
{
    public Guid ResultId { get; set; }
    public Guid SetId { get; set; }
    public string SetVersion { get; set; } = string.Empty;
    public DateTime RunAt { get; set; }
    public int PassCount { get; set; }
    public int FailCount { get; set; }
    public string Regressions { get; set; } = "[]";
    public bool PromotionBlockFlag { get; set; }
    public string? CorrelationId { get; set; }
}
