namespace Muallimi.Application.Audit;

/// <summary>
/// Phase 1 telemetry field constants for structured logging.
/// Used across main-backend for consistent field naming in logs and traces.
/// </summary>
public static class TelemetryFields
{
    // Phase 0 baseline fields
    public const string CorrelationId = "correlation_id";
    public const string TenantId = "tenant_id";
    public const string ActorId = "actor_id";
    public const string RepositoryId = "repository_id";
    public const string Environment = "environment";

    // Phase 1 curriculum fields
    public const string CurriculumType = "curriculum_type";
    public const string Grade = "grade";
    public const string Subject = "subject";
    public const string LessonId = "lesson_id";
    public const string AssetId = "asset_id";
    public const string AssetType = "asset_type";
    public const string Outcome = "outcome";

    // Phase 1 cost tracking fields
    public const string CostUnits = "cost_units";
    public const string CostAmount = "cost_amount";
    public const string CostProvider = "cost_provider";

    // Phase 1 job tracking
    public const string JobId = "job_id";
    public const string JobType = "job_type";
    public const string JobStage = "job_stage";
    public const string JobAttempt = "job_attempt";

    // Repository identifier for this service
    public const string MainBackendRepository = "main-backend";

    // ── Phase 2: AI tutor, guardrail chain, routing, prompt registry, telemetry ──
    public const string TutorLanguage = "tutor_language";
    public const string SessionMode = "session_mode";
    public const string Stage = "stage";
    public const string Decision = "decision";
    public const string ClassifierId = "classifier_id";
    public const string ClassifierVersion = "classifier_version";
    public const string PromptId = "prompt_id";
    public const string VersionId = "version_id";
    public const string ModelTier = "model_tier";
    public const string ProviderIdentifier = "provider_identifier";
    public const string InputTokens = "input_tokens";
    public const string OutputTokens = "output_tokens";
    public const string LatencyMs = "latency_ms";
    public const string CacheMatchScore = "cache_match_score";
    public const string FinalOutcome = "final_outcome";
}
