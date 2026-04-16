using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.AiOperations;
using Muallimi.Domain.Content;
using Muallimi.Domain.Coverage;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Domain.ProviderBindings;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;

namespace Muallimi.Infrastructure.Persistence;

public class MuallimiDbContext : DbContext
{
    public MuallimiDbContext(DbContextOptions<MuallimiDbContext> options) : base(options) { }

    // Curriculum
    public DbSet<CurriculumSource> CurriculumSources => Set<CurriculumSource>();
    public DbSet<CurriculumStructure> CurriculumStructures => Set<CurriculumStructure>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<ContentChunk> ContentChunks => Set<ContentChunk>();
    public DbSet<QaCacheEntry> QaCacheEntries => Set<QaCacheEntry>();
    public DbSet<ChangeLogEntry> ChangeLogEntries => Set<ChangeLogEntry>();

    // Content
    public DbSet<GeneratedAsset> GeneratedAssets => Set<GeneratedAsset>();
    public DbSet<FormatDecision> FormatDecisions => Set<FormatDecision>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();
    public DbSet<AutoValidationResult> AutoValidationResults => Set<AutoValidationResult>();

    // Review
    public DbSet<ReviewAssignment> ReviewAssignments => Set<ReviewAssignment>();
    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();

    // Publication
    public DbSet<PublishedAsset> PublishedAssets => Set<PublishedAsset>();

    // Coverage
    public DbSet<CoverageStatus> CoverageStatuses => Set<CoverageStatus>();

    // ── Phase 2: AI Tutor, Prompts, Provider Bindings, AI Operations ──
    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();
    public DbSet<PromptAuditEntry> PromptAuditEntries => Set<PromptAuditEntry>();
    public DbSet<ProviderAdapterBinding> ProviderAdapterBindings => Set<ProviderAdapterBinding>();
    public DbSet<AiRequestRecord> AiRequestRecords => Set<AiRequestRecord>();
    public DbSet<RefusalEvent> RefusalEvents => Set<RefusalEvent>();
    public DbSet<AiOperationsMetric> AiOperationsMetrics => Set<AiOperationsMetric>();
    public DbSet<RedTeamScenarioSet> RedTeamScenarioSets => Set<RedTeamScenarioSet>();
    public DbSet<RedTeamEvaluationResult> RedTeamEvaluationResults => Set<RedTeamEvaluationResult>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Enable pgvector extension
        modelBuilder.HasPostgresExtension("vector");

        // Use snake_case naming convention for all tables and columns
        // Configure each entity

        // ── CurriculumSource ──
        modelBuilder.Entity<CurriculumSource>(e =>
        {
            e.ToTable("curriculum_sources");
            e.HasKey(x => x.SourceId);
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type").HasConversion<string>();
            e.Property(x => x.Grade).HasColumnName("grade").HasConversion<string>();
            e.Property(x => x.Subject).HasColumnName("subject").HasConversion<string>();
            e.Property(x => x.AcademicYear).HasColumnName("academic_year");
            e.Property(x => x.TutorLanguage).HasColumnName("tutor_language").HasConversion<string>();
            e.Property(x => x.FileFormat).HasColumnName("file_format").HasConversion<string>();
            e.Property(x => x.StorageKey).HasColumnName("storage_key");
            e.Property(x => x.UploadActor).HasColumnName("upload_actor");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            e.Property(x => x.ContentHash).HasColumnName("content_hash");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        });

        // ── CurriculumStructure ──
        modelBuilder.Entity<CurriculumStructure>(e =>
        {
            e.ToTable("curriculum_structures");
            e.HasKey(x => x.StructureId);
            e.Property(x => x.StructureId).HasColumnName("structure_id");
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.Nodes).HasColumnName("nodes").HasColumnType("jsonb");
            e.Property(x => x.ExtractedAt).HasColumnName("extracted_at");

            e.HasOne(x => x.Source)
                .WithMany()
                .HasForeignKey(x => x.SourceId);
        });

        // ── Lesson ──
        modelBuilder.Entity<Lesson>(e =>
        {
            e.ToTable("lessons");
            e.HasKey(x => x.LessonId);
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.StructureId).HasColumnName("structure_id");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type").HasConversion<string>();
            e.Property(x => x.Grade).HasColumnName("grade").HasConversion<string>();
            e.Property(x => x.Subject).HasColumnName("subject").HasConversion<string>();
            e.Property(x => x.TutorLanguage).HasColumnName("tutor_language").HasConversion<string>();
            e.Property(x => x.Path).HasColumnName("path");
            e.Property(x => x.ContentHash).HasColumnName("content_hash");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.Property(x => x.LastChangeReason).HasColumnName("last_change_reason");

            e.HasOne(x => x.Structure)
                .WithMany()
                .HasForeignKey(x => x.StructureId);
        });

        // ── ContentChunk ──
        modelBuilder.Entity<ContentChunk>(e =>
        {
            e.ToTable("content_chunks");
            e.HasKey(x => x.ChunkId);
            e.Property(x => x.ChunkId).HasColumnName("chunk_id");
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.Sequence).HasColumnName("sequence");
            e.Property(x => x.Text).HasColumnName("text");
            e.Property(x => x.MathBlocks).HasColumnName("math_blocks").HasColumnType("jsonb");
            e.Property(x => x.TokenCount).HasColumnName("token_count");
            e.Property(x => x.OverlapWithPrevious).HasColumnName("overlap_with_previous");
            e.Property(x => x.SourceRefs).HasColumnName("source_refs").HasColumnType("jsonb");
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.Embedding).HasColumnName("embedding").HasColumnType("vector(1536)");
            e.Property(x => x.EmbeddingModelVersion).HasColumnName("embedding_model_version");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();

            e.HasOne(x => x.Lesson)
                .WithMany()
                .HasForeignKey(x => x.LessonId);
        });

        // ── QaCacheEntry ──
        modelBuilder.Entity<QaCacheEntry>(e =>
        {
            e.ToTable("qa_cache_entries");
            e.HasKey(x => x.EntryId);
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type").HasConversion<string>();
            e.Property(x => x.Subject).HasColumnName("subject").HasConversion<string>();
            e.Property(x => x.Topic).HasColumnName("topic");
            e.Property(x => x.Grade).HasColumnName("grade").HasConversion<string>();
            e.Property(x => x.TutorLanguage).HasColumnName("tutor_language").HasConversion<string>();
            e.Property(x => x.QuestionText).HasColumnName("question_text");
            e.Property(x => x.QuestionEmbedding).HasColumnName("question_embedding").HasColumnType("vector(1536)");
            e.Property(x => x.AnswerText).HasColumnName("answer_text");
            e.Property(x => x.SourceChunkIds).HasColumnName("source_chunk_ids").HasColumnType("jsonb");
            e.Property(x => x.ModelVersion).HasColumnName("model_version");
            e.Property(x => x.ValidationStatus).HasColumnName("validation_status").HasConversion<string>();
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.LastReviewedAt).HasColumnName("last_reviewed_at");
        });

        // ── ChangeLogEntry ──
        modelBuilder.Entity<ChangeLogEntry>(e =>
        {
            e.ToTable("change_log_entries");
            e.HasKey(x => x.EntryId);
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasConversion<string>();
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        });

        // ── GeneratedAsset ──
        modelBuilder.Entity<GeneratedAsset>(e =>
        {
            e.ToTable("generated_assets");
            e.HasKey(x => x.AssetId);
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.AssetType).HasColumnName("asset_type").HasConversion<string>();
            e.Property(x => x.VisualFormat).HasColumnName("visual_format").HasConversion<string?>();
            e.Property(x => x.StorageKey).HasColumnName("storage_key");
            e.Property(x => x.Transcript).HasColumnName("transcript");
            e.Property(x => x.Language).HasColumnName("language");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.ProducedBy).HasColumnName("produced_by");
            e.Property(x => x.ProducedAt).HasColumnName("produced_at");
            e.Property(x => x.Cost).HasColumnName("cost").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        });

        // ── FormatDecision ──
        modelBuilder.Entity<FormatDecision>(e =>
        {
            e.ToTable("format_decisions");
            e.HasKey(x => x.DecisionId);
            e.Property(x => x.DecisionId).HasColumnName("decision_id");
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.SelectedFormat).HasColumnName("selected_format").HasConversion<string>();
            e.Property(x => x.RuleTriggered).HasColumnName("rule_triggered");
            e.Property(x => x.LlmRefinement).HasColumnName("llm_refinement");
            e.Property(x => x.OverriddenBy).HasColumnName("overridden_by");
            e.Property(x => x.OverriddenAt).HasColumnName("overridden_at");
        });

        // ── IngestionJob ──
        modelBuilder.Entity<IngestionJob>(e =>
        {
            e.ToTable("ingestion_jobs");
            e.HasKey(x => x.JobId);
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.Stages).HasColumnName("stages").HasColumnType("jsonb");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.ErrorReason).HasColumnName("error_reason");
        });

        // ── GenerationJob ──
        modelBuilder.Entity<GenerationJob>(e =>
        {
            e.ToTable("generation_jobs");
            e.HasKey(x => x.JobId);
            e.Property(x => x.JobId).HasColumnName("job_id");
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
            e.Property(x => x.Stages).HasColumnName("stages").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CostSummary).HasColumnName("cost_summary").HasColumnType("jsonb");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.ErrorReason).HasColumnName("error_reason");
        });

        // ── AutoValidationResult ──
        modelBuilder.Entity<AutoValidationResult>(e =>
        {
            e.ToTable("auto_validation_results");
            e.HasKey(x => x.ResultId);
            e.Property(x => x.ResultId).HasColumnName("result_id");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.Checks).HasColumnName("checks").HasColumnType("jsonb");
            e.Property(x => x.GroundingEvidence).HasColumnName("grounding_evidence").HasColumnType("jsonb");
            e.Property(x => x.ArabicQuality).HasColumnName("arabic_quality").HasColumnType("jsonb");
            e.Property(x => x.Rendering).HasColumnName("rendering").HasColumnType("jsonb");
            e.Property(x => x.NarrationSync).HasColumnName("narration_sync").HasColumnType("jsonb");
            e.Property(x => x.Accessibility).HasColumnName("accessibility").HasColumnType("jsonb");
            e.Property(x => x.Alignment).HasColumnName("alignment").HasColumnType("jsonb");
            e.Property(x => x.Decision).HasColumnName("decision").HasConversion<string>();
            e.Property(x => x.ValidatedAt).HasColumnName("validated_at");
        });

        // ── ReviewAssignment ──
        modelBuilder.Entity<ReviewAssignment>(e =>
        {
            e.ToTable("review_assignments");
            e.HasKey(x => x.AssignmentId);
            e.Property(x => x.AssignmentId).HasColumnName("assignment_id");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.Tier).HasColumnName("tier").HasConversion<string>();
            e.Property(x => x.AssignedTo).HasColumnName("assigned_to");
            e.Property(x => x.AssignedBy).HasColumnName("assigned_by");
            e.Property(x => x.AssignedAt).HasColumnName("assigned_at");
            e.Property(x => x.SlaDueAt).HasColumnName("sla_due_at");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        });

        // ── ReviewDecision ──
        modelBuilder.Entity<ReviewDecision>(e =>
        {
            e.ToTable("review_decisions");
            e.HasKey(x => x.DecisionId);
            e.Property(x => x.DecisionId).HasColumnName("decision_id");
            e.Property(x => x.AssetId).HasColumnName("asset_id");
            e.Property(x => x.Tier).HasColumnName("tier").HasConversion<string>();
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<string>();
            e.Property(x => x.Scope).HasColumnName("scope");
            e.Property(x => x.FixInstruction).HasColumnName("fix_instruction");
            e.Property(x => x.DecidedAt).HasColumnName("decided_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        });

        // ── PublishedAsset ──
        modelBuilder.Entity<PublishedAsset>(e =>
        {
            e.ToTable("published_assets");
            e.HasKey(x => x.PublishedId);
            e.Property(x => x.PublishedId).HasColumnName("published_id");
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.AssetType).HasColumnName("asset_type").HasConversion<string>();
            e.Property(x => x.VisualFormat).HasColumnName("visual_format").HasConversion<string?>();
            e.Property(x => x.RuntimeUrl).HasColumnName("runtime_url");
            e.Property(x => x.ApprovedByAdmin).HasColumnName("approved_by_admin");
            e.Property(x => x.ApprovedByExpert).HasColumnName("approved_by_expert");
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
        });

        // ── CoverageStatus ──
        modelBuilder.Entity<CoverageStatus>(e =>
        {
            e.ToTable("coverage_statuses");
            e.HasKey(x => new { x.LessonId, x.AssetType });
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.AssetType).HasColumnName("asset_type").HasConversion<string>();
            e.Property(x => x.State).HasColumnName("state").HasConversion<string>();
            e.Property(x => x.QueueAge).HasColumnName("queue_age");
            e.Property(x => x.Owner).HasColumnName("owner");
            e.Property(x => x.LastUpdatedAt).HasColumnName("last_updated_at");
        });

        // ── Phase 2: Prompt ──
        modelBuilder.Entity<Prompt>(e =>
        {
            e.ToTable("prompts");
            e.HasKey(x => x.PromptId);
            e.Property(x => x.PromptId).HasColumnName("prompt_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.Scope).HasColumnName("scope");
            e.Property(x => x.ActiveVersionId).HasColumnName("active_version_id");
            e.Property(x => x.PromotionBlockFlag).HasColumnName("promotion_block_flag");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.Name).IsUnique();
        });

        // ── Phase 2: PromptVersion ──
        modelBuilder.Entity<PromptVersion>(e =>
        {
            e.ToTable("prompt_versions");
            e.HasKey(x => x.VersionId);
            e.Property(x => x.VersionId).HasColumnName("version_id");
            e.Property(x => x.PromptId).HasColumnName("prompt_id");
            e.Property(x => x.VersionNumber).HasColumnName("version_number");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.DeclaredVariables).HasColumnName("declared_variables").HasColumnType("jsonb");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Status).HasColumnName("status");
            e.HasIndex(x => new { x.PromptId, x.VersionNumber }).IsUnique();
        });

        // ── Phase 2: PromptAuditEntry ──
        modelBuilder.Entity<PromptAuditEntry>(e =>
        {
            e.ToTable("prompt_audit_entries");
            e.HasKey(x => x.EntryId);
            e.Property(x => x.EntryId).HasColumnName("entry_id");
            e.Property(x => x.PromptId).HasColumnName("prompt_id");
            e.Property(x => x.VersionId).HasColumnName("version_id");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.Actor).HasColumnName("actor");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.Diff).HasColumnName("diff");
            e.HasIndex(x => x.PromptId);
        });

        // ── Phase 2: ProviderAdapterBinding ──
        modelBuilder.Entity<ProviderAdapterBinding>(e =>
        {
            e.ToTable("provider_adapter_bindings");
            e.HasKey(x => x.BindingId);
            e.Property(x => x.BindingId).HasColumnName("binding_id");
            e.Property(x => x.Capability).HasColumnName("capability");
            e.Property(x => x.Environment).HasColumnName("environment");
            e.Property(x => x.CurriculumScope).HasColumnName("curriculum_scope");
            e.Property(x => x.ProviderIdentifier).HasColumnName("provider_identifier");
            e.Property(x => x.ProviderConfigurationRef).HasColumnName("provider_configuration_ref");
            e.Property(x => x.FallbackChain).HasColumnName("fallback_chain").HasColumnType("jsonb");
            e.Property(x => x.Active).HasColumnName("active");
            e.Property(x => x.PromotionBlockFlag).HasColumnName("promotion_block_flag");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            // T089: exactly one active binding per (capability, environment, curriculum_scope)
            e.HasIndex(x => new { x.Capability, x.Environment, x.CurriculumScope })
                .HasFilter("active = true")
                .IsUnique();
            e.HasIndex(x => new { x.Capability, x.Environment, x.CurriculumScope, x.Active });
        });

        // ── Phase 2: AiRequestRecord ──
        modelBuilder.Entity<AiRequestRecord>(e =>
        {
            e.ToTable("ai_request_records");
            e.HasKey(x => x.RecordId);
            e.Property(x => x.RecordId).HasColumnName("record_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.SessionId).HasColumnName("session_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type");
            e.Property(x => x.Grade).HasColumnName("grade");
            e.Property(x => x.Subject).HasColumnName("subject");
            e.Property(x => x.TutorLanguage).HasColumnName("tutor_language");
            e.Property(x => x.SessionMode).HasColumnName("session_mode");
            e.Property(x => x.Stages).HasColumnName("stages").HasColumnType("jsonb");
            e.Property(x => x.RoutingDecision).HasColumnName("routing_decision").HasColumnType("jsonb");
            e.Property(x => x.InputTokenCount).HasColumnName("input_token_count");
            e.Property(x => x.OutputTokenCount).HasColumnName("output_token_count");
            e.Property(x => x.LatencyMs).HasColumnName("latency_ms");
            e.Property(x => x.CacheMatchScore).HasColumnName("cache_match_score");
            e.Property(x => x.FinalOutcome).HasColumnName("final_outcome");
            e.Property(x => x.QuestionTextPreview).HasColumnName("question_text_preview");
            e.Property(x => x.PromptVersionsUsed).HasColumnName("prompt_versions_used").HasColumnType("jsonb");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => new { x.CorrelationId, x.SessionId, x.CurriculumType, x.FinalOutcome });
        });

        // ── Phase 2: RefusalEvent ──
        modelBuilder.Entity<RefusalEvent>(e =>
        {
            e.ToTable("refusal_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.RecordId).HasColumnName("record_id");
            e.Property(x => x.Stage).HasColumnName("stage");
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.LocalisedReason).HasColumnName("localised_reason");
            e.Property(x => x.TutorLanguage).HasColumnName("tutor_language");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => x.RecordId);
        });

        // ── Phase 2: AiOperationsMetric ──
        modelBuilder.Entity<AiOperationsMetric>(e =>
        {
            e.ToTable("ai_operations_metrics");
            e.HasKey(x => x.MetricId);
            e.Property(x => x.MetricId).HasColumnName("metric_id");
            e.Property(x => x.WindowStart).HasColumnName("window_start");
            e.Property(x => x.WindowEnd).HasColumnName("window_end");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type");
            e.Property(x => x.Grade).HasColumnName("grade");
            e.Property(x => x.Subject).HasColumnName("subject");
            e.Property(x => x.TutorLanguage).HasColumnName("tutor_language");
            e.Property(x => x.SessionMode).HasColumnName("session_mode");
            e.Property(x => x.Volume).HasColumnName("volume");
            e.Property(x => x.RefusalRate).HasColumnName("refusal_rate");
            e.Property(x => x.CacheHitRate).HasColumnName("cache_hit_rate");
            e.Property(x => x.GroundedAnswerRate).HasColumnName("grounded_answer_rate");
            e.Property(x => x.PerBranch).HasColumnName("per_branch").HasColumnType("jsonb");
            e.Property(x => x.PromptVersionDistribution).HasColumnName("prompt_version_distribution").HasColumnType("jsonb");
            e.Property(x => x.ComputedAt).HasColumnName("computed_at");
        });

        // ── Phase 2: RedTeamScenarioSet ──
        modelBuilder.Entity<RedTeamScenarioSet>(e =>
        {
            e.ToTable("red_team_scenario_sets");
            e.HasKey(x => x.SetId);
            e.Property(x => x.SetId).HasColumnName("set_id");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.StorageKey).HasColumnName("storage_key");
            e.Property(x => x.ScenarioCount).HasColumnName("scenario_count");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // ── Phase 2: RedTeamEvaluationResult ──
        modelBuilder.Entity<RedTeamEvaluationResult>(e =>
        {
            e.ToTable("red_team_evaluation_results");
            e.HasKey(x => x.ResultId);
            e.Property(x => x.ResultId).HasColumnName("result_id");
            e.Property(x => x.SetId).HasColumnName("set_id");
            e.Property(x => x.SetVersion).HasColumnName("set_version");
            e.Property(x => x.RunAt).HasColumnName("run_at");
            e.Property(x => x.PassCount).HasColumnName("pass_count");
            e.Property(x => x.FailCount).HasColumnName("fail_count");
            e.Property(x => x.Regressions).HasColumnName("regressions").HasColumnType("jsonb");
            e.Property(x => x.PromotionBlockFlag).HasColumnName("promotion_block_flag");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        });
    }
}
