using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.AiOperations;
using Muallimi.Domain.Content;
using Muallimi.Domain.Coverage;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Domain.ProviderBindings;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Review;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Domain.SaasOperations;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Identity.EfCore;

namespace Muallimi.Infrastructure.Persistence;

/// <summary>
/// Ambient tenant accessor contract. Supplied by Phase 3 Api-layer
/// HttpTenantContextAccessor at runtime, or by test doubles in unit/contract
/// tests. Null value means "no tenant scope" — filter matches nothing.
/// </summary>
public interface IDbTenantContextAccessor
{
    Guid? CurrentTenantId { get; }
}

public sealed class NullTenantContextAccessor : IDbTenantContextAccessor
{
    public Guid? CurrentTenantId => null;
}

public class MuallimiDbContext : DbContext
{
    private readonly IDbTenantContextAccessor _tenantContextAccessor;

    public MuallimiDbContext(DbContextOptions<MuallimiDbContext> options)
        : this(options, new NullTenantContextAccessor()) { }

    public MuallimiDbContext(
        DbContextOptions<MuallimiDbContext> options,
        IDbTenantContextAccessor tenantContextAccessor) : base(options)
    {
        _tenantContextAccessor = tenantContextAccessor;
    }

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

    // ── Phase 3: Student Learning Experience ──
    public DbSet<StudentProfile> StudentProfiles => Set<StudentProfile>();
    public DbSet<StudentSession> StudentSessions => Set<StudentSession>();
    public DbSet<LessonViewerState> LessonViewerStates => Set<LessonViewerState>();
    public DbSet<TutorChatMessage> TutorChatMessages => Set<TutorChatMessage>();
    public DbSet<VoiceCapture> VoiceCaptures => Set<VoiceCapture>();
    public DbSet<QuizSession> QuizSessions => Set<QuizSession>();
    public DbSet<MockTestSession> MockTestSessions => Set<MockTestSession>();
    public DbSet<HomeworkHelpSubmission> HomeworkHelpSubmissions => Set<HomeworkHelpSubmission>();
    public DbSet<WhiteboardSession> WhiteboardSessions => Set<WhiteboardSession>();
    public DbSet<PlanGatePolicy> PlanGatePolicies => Set<PlanGatePolicy>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    // ── Phase 4: Engagement, Progress, and Parent Support ──
    public DbSet<ProgressRecord> ProgressRecords => Set<ProgressRecord>();
    public DbSet<MasteryState> MasteryStates => Set<MasteryState>();
    public DbSet<StreakState> StreakStates => Set<StreakState>();
    public DbSet<BadgeCriterion> BadgeCriteria => Set<BadgeCriterion>();
    public DbSet<BadgeAward> BadgeAwards => Set<BadgeAward>();
    public DbSet<FocusArea> FocusAreas => Set<FocusArea>();
    public DbSet<WeeklyReport> WeeklyReports => Set<WeeklyReport>();
    public DbSet<GuardrailDecisionTrail> GuardrailDecisionTrails => Set<GuardrailDecisionTrail>();
    public DbSet<AtRiskFlag> AtRiskFlags => Set<AtRiskFlag>();
    public DbSet<InterventionPrompt> InterventionPrompts => Set<InterventionPrompt>();
    public DbSet<Phase4DownstreamEvent> Phase4DownstreamEvents => Set<Phase4DownstreamEvent>();
    public DbSet<ParentProfile> ParentProfiles => Set<ParentProfile>();
    public DbSet<ChildLink> ChildLinks => Set<ChildLink>();
    public DbSet<ParentNotification> ParentNotifications => Set<ParentNotification>();
    public DbSet<OperatorImpersonationAudit> OperatorImpersonationAudits => Set<OperatorImpersonationAudit>();
    public DbSet<ProgressIngestionDeadLetter> ProgressIngestionDeadLetters => Set<ProgressIngestionDeadLetter>();

    // ── Phase 5: School Management and B2B Administration ──
    public DbSet<SchoolTenant> SchoolTenants => Set<SchoolTenant>();
    public DbSet<SchoolAdministrator> SchoolAdministrators => Set<SchoolAdministrator>();
    public DbSet<Teacher> Teachers => Set<Teacher>();
    public DbSet<ClassGroup> ClassGroups => Set<ClassGroup>();
    public DbSet<ClassEnrolment> ClassEnrolments => Set<ClassEnrolment>();
    public DbSet<TeacherAssignment> TeacherAssignments => Set<TeacherAssignment>();
    public DbSet<RosterImport> RosterImports => Set<RosterImport>();
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<ExamQuestion> ExamQuestions => Set<ExamQuestion>();
    public DbSet<ExamAssignment> ExamAssignments => Set<ExamAssignment>();
    public DbSet<ExamSubmission> ExamSubmissions => Set<ExamSubmission>();
    public DbSet<LeaderboardSnapshot> LeaderboardSnapshots => Set<LeaderboardSnapshot>();
    public DbSet<LeaderboardConfig> LeaderboardConfigs => Set<LeaderboardConfig>();
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementDelivery> AnnouncementDeliveries => Set<AnnouncementDelivery>();
    public DbSet<SchoolReport> SchoolReports => Set<SchoolReport>();
    public DbSet<SchoolLicense> SchoolLicenses => Set<SchoolLicense>();
    public DbSet<SchoolAggregateView> SchoolAggregateViews => Set<SchoolAggregateView>();
    public DbSet<Phase5DownstreamEvent> Phase5DownstreamEvents => Set<Phase5DownstreamEvent>();

    // ── Phase 6: SaaS Operations, Billing, Security, and Launch Readiness ──
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();
    public DbSet<NotificationProviderBinding> NotificationProviderBindings => Set<NotificationProviderBinding>();
    public DbSet<NotificationDeliveryReceipt> NotificationDeliveryReceipts => Set<NotificationDeliveryReceipt>();
    public DbSet<AIOperationsMetric> Phase6AIOperationsMetrics => Set<AIOperationsMetric>();
    public DbSet<AIOperationsAggregate> AIOperationsAggregates => Set<AIOperationsAggregate>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<IncidentRecord> IncidentRecords => Set<IncidentRecord>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<DataDeletionRequest> DataDeletionRequests => Set<DataDeletionRequest>();
    public DbSet<DataRetentionPolicy> DataRetentionPolicies => Set<DataRetentionPolicy>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<LaunchReadinessGate> LaunchReadinessGates => Set<LaunchReadinessGate>();
    public DbSet<TenantHealthView> TenantHealthViews => Set<TenantHealthView>();
    public DbSet<Phase6OperationalEvent> Phase6OperationalEvents => Set<Phase6OperationalEvent>();

    // ── Phase 9: Identity & Authentication ──
    public DbSet<Tenant> IdentityTenants => Set<Tenant>();
    public DbSet<User> IdentityUsers => Set<User>();
    public DbSet<Role> IdentityRoles => Set<Role>();
    public DbSet<UserRole> IdentityUserRoles => Set<UserRole>();
    public DbSet<RefreshToken> IdentityRefreshTokens => Set<RefreshToken>();
    public DbSet<UserSession> IdentityUserSessions => Set<UserSession>();
    public DbSet<LoginAttempt> IdentityLoginAttempts => Set<LoginAttempt>();
    public DbSet<EmailVerificationToken> IdentityEmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<PasswordResetToken> IdentityPasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<TwoFactorSecret> IdentityTwoFactorSecrets => Set<TwoFactorSecret>();
    public DbSet<ImpersonationSession> IdentityImpersonationSessions => Set<ImpersonationSession>();
    public DbSet<BackfillError> IdentityBackfillErrors => Set<BackfillError>();

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
            e.Property(x => x.OriginalFileName).HasColumnName("original_file_name");
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

        ConfigurePhase3(modelBuilder);
        ApplyPhase3TenantFilters(modelBuilder);

        ConfigurePhase4(modelBuilder);
        ApplyPhase4TenantFilters(modelBuilder);

        ConfigurePhase5(modelBuilder);
        ApplyPhase5TenantFilters(modelBuilder);

        ConfigurePhase6(modelBuilder);
        ApplyPhase6TenantFilters(modelBuilder);

        modelBuilder.ConfigurePhase9Identity();
        ApplyPhase9IdentityTenantFilters(modelBuilder);

        // Phase 9 additive: nullable legacy-link FK columns on existing person entities.
        modelBuilder.Entity<StudentProfile>()
            .Property(x => x.UserId).HasColumnName("user_id");
        modelBuilder.Entity<ParentProfile>()
            .Property(x => x.UserId).HasColumnName("user_id");
        modelBuilder.Entity<SchoolAdministrator>()
            .Property(x => x.UserId).HasColumnName("user_id");
        modelBuilder.Entity<Teacher>()
            .Property(x => x.UserId).HasColumnName("user_id");
    }

    private void ApplyPhase9IdentityTenantFilters(ModelBuilder modelBuilder)
    {
        ApplyTenantFilter<User>(modelBuilder);
        ApplyTenantFilter<UserRole>(modelBuilder);
    }

    private static void ConfigurePhase3(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StudentProfile>(e =>
        {
            e.ToTable("student_profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.AvatarReference).HasColumnName("avatar_reference");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type");
            e.Property(x => x.Grade).HasColumnName("grade");
            e.Property(x => x.PreferredLanguage).HasColumnName("preferred_language");
            e.Property(x => x.RtlOverride).HasColumnName("rtl_override");
            e.Property(x => x.PlanTier).HasColumnName("plan_tier");
            e.Property(x => x.SubjectsEnrolled).HasColumnName("subjects_enrolled").HasColumnType("jsonb");
            e.Property(x => x.ConsentState).HasColumnName("consent_state");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<StudentSession>(e =>
        {
            e.ToTable("student_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentProfileId).HasColumnName("student_profile_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.ActiveCurriculumType).HasColumnName("active_curriculum_type");
            e.Property(x => x.ActiveGrade).HasColumnName("active_grade");
            e.Property(x => x.ActiveSubjectId).HasColumnName("active_subject_id");
            e.Property(x => x.ActiveChapterId).HasColumnName("active_chapter_id");
            e.Property(x => x.ActiveTopicId).HasColumnName("active_topic_id");
            e.Property(x => x.ActiveLessonId).HasColumnName("active_lesson_id");
            e.Property(x => x.ActiveMode).HasColumnName("active_mode");
            e.Property(x => x.TutorLanguage).HasColumnName("tutor_language");
            e.Property(x => x.DeviceClass).HasColumnName("device_class");
            e.Property(x => x.PlanTierSnapshot).HasColumnName("plan_tier_snapshot");
            e.Property(x => x.SessionStartedAt).HasColumnName("session_started_at");
            e.Property(x => x.SessionLastActivityAt).HasColumnName("session_last_activity_at");
            e.Property(x => x.SessionEndedAt).HasColumnName("session_ended_at");
            e.Property(x => x.EndReason).HasColumnName("end_reason");
            e.HasIndex(x => new { x.TenantId, x.StudentProfileId });
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<LessonViewerState>(e =>
        {
            e.ToTable("lesson_viewer_states");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.LessonId).HasColumnName("lesson_id");
            e.Property(x => x.ViewerPosition).HasColumnName("viewer_position").HasColumnType("jsonb");
            e.Property(x => x.PlaybackState).HasColumnName("playback_state");
            e.Property(x => x.TeacherVoiceProfileId).HasColumnName("teacher_voice_profile_id");
            e.Property(x => x.CaptionsEnabled).HasColumnName("captions_enabled");
            e.Property(x => x.Rate).HasColumnName("rate");
            e.Property(x => x.LastInteractionAt).HasColumnName("last_interaction_at");
            e.HasIndex(x => new { x.TenantId, x.StudentSessionId });
        });

        modelBuilder.Entity<TutorChatMessage>(e =>
        {
            e.ToTable("tutor_chat_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.TurnNumber).HasColumnName("turn_number");
            e.Property(x => x.Role).HasColumnName("role");
            e.Property(x => x.Modality).HasColumnName("modality");
            e.Property(x => x.Language).HasColumnName("language");
            e.Property(x => x.ContentText).HasColumnName("content_text");
            e.Property(x => x.VoiceCaptureReference).HasColumnName("voice_capture_reference");
            e.Property(x => x.VoicePlaybackReference).HasColumnName("voice_playback_reference");
            e.Property(x => x.AiRequestRecordId).HasColumnName("ai_request_record_id");
            e.Property(x => x.GuardrailFinalStage).HasColumnName("guardrail_final_stage");
            e.Property(x => x.FinalOutcome).HasColumnName("final_outcome");
            e.Property(x => x.ConfidenceSignal).HasColumnName("confidence_signal");
            e.Property(x => x.EvidenceRefs).HasColumnName("evidence_refs").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.StudentSessionId, x.TurnNumber }).IsUnique();
        });

        modelBuilder.Entity<VoiceCapture>(e =>
        {
            e.ToTable("voice_captures");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.TutorChatMessageId).HasColumnName("tutor_chat_message_id");
            e.Property(x => x.BlobReference).HasColumnName("blob_reference");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.Codec).HasColumnName("codec");
            e.Property(x => x.UploadState).HasColumnName("upload_state");
            e.Property(x => x.SttState).HasColumnName("stt_state");
            e.Property(x => x.TranscriptText).HasColumnName("transcript_text");
            e.Property(x => x.SttAdapterBindingId).HasColumnName("stt_adapter_binding_id");
            e.Property(x => x.RetentionUntil).HasColumnName("retention_until");
            e.HasIndex(x => x.StudentSessionId);
        });

        modelBuilder.Entity<QuizSession>(e =>
        {
            e.ToTable("quiz_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.ChapterId).HasColumnName("chapter_id");
            e.Property(x => x.TopicId).HasColumnName("topic_id");
            e.Property(x => x.QuestionBankSnapshot).HasColumnName("question_bank_snapshot").HasColumnType("jsonb");
            e.Property(x => x.Progress).HasColumnName("progress").HasColumnType("jsonb");
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.HasIndex(x => x.StudentSessionId);
        });

        modelBuilder.Entity<MockTestSession>(e =>
        {
            e.ToTable("mock_test_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.QuestionBankSnapshot).HasColumnName("question_bank_snapshot").HasColumnType("jsonb");
            e.Property(x => x.TimeLimitSeconds).HasColumnName("time_limit_seconds");
            e.Property(x => x.ServerStartedAt).HasColumnName("server_started_at");
            e.Property(x => x.ServerDeadlineAt).HasColumnName("server_deadline_at");
            e.Property(x => x.Progress).HasColumnName("progress").HasColumnType("jsonb");
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.PlanTierSnapshot).HasColumnName("plan_tier_snapshot");
            e.Property(x => x.FinalScore).HasColumnName("final_score");
            e.HasIndex(x => x.StudentSessionId);
        });

        modelBuilder.Entity<HomeworkHelpSubmission>(e =>
        {
            e.ToTable("homework_help_submissions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.InputModality).HasColumnName("input_modality");
            e.Property(x => x.TextPayload).HasColumnName("text_payload");
            e.Property(x => x.VoiceCaptureId).HasColumnName("voice_capture_id");
            e.Property(x => x.ImageBlobReference).HasColumnName("image_blob_reference");
            e.Property(x => x.ImagePreprocessMetadata).HasColumnName("image_preprocess_metadata").HasColumnType("jsonb");
            e.Property(x => x.OcrAdapterBindingId).HasColumnName("ocr_adapter_binding_id");
            e.Property(x => x.ExtractedProblemText).HasColumnName("extracted_problem_text");
            e.Property(x => x.AiRequestRecordId).HasColumnName("ai_request_record_id");
            e.Property(x => x.FinalOutcome).HasColumnName("final_outcome");
            e.Property(x => x.RetentionUntil).HasColumnName("retention_until");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.StudentSessionId);
        });

        modelBuilder.Entity<WhiteboardSession>(e =>
        {
            e.ToTable("whiteboard_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.TopicId).HasColumnName("topic_id");
            e.Property(x => x.PlanTierSnapshot).HasColumnName("plan_tier_snapshot");
            e.Property(x => x.SessionMode).HasColumnName("session_mode");
            e.Property(x => x.StepLog).HasColumnName("step_log").HasColumnType("jsonb");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.Property(x => x.EndReason).HasColumnName("end_reason");
            e.HasIndex(x => x.StudentSessionId);
        });

        modelBuilder.Entity<PlanGatePolicy>(e =>
        {
            e.ToTable("plan_gate_policies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Mode).HasColumnName("mode");
            e.Property(x => x.RequiredPlanTiers).HasColumnName("required_plan_tiers").HasColumnType("jsonb");
            e.Property(x => x.SubjectScope).HasColumnName("subject_scope").HasColumnType("jsonb");
            e.Property(x => x.GradeScope).HasColumnName("grade_scope").HasColumnType("jsonb");
            e.Property(x => x.EnabledAt).HasColumnName("enabled_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.PolicySource).HasColumnName("policy_source");
            e.HasIndex(x => new { x.Mode, x.TenantId, x.EnabledAt });
        });

        modelBuilder.Entity<SessionEvent>(e =>
        {
            e.ToTable("session_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentSessionId).HasColumnName("student_session_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.EventKind).HasColumnName("event_kind");
            e.Property(x => x.EventPayload).HasColumnName("event_payload").HasColumnType("jsonb");
            e.Property(x => x.CurriculumScope).HasColumnName("curriculum_scope").HasColumnType("jsonb");
            e.Property(x => x.PlanTierSnapshot).HasColumnName("plan_tier_snapshot");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            e.Property(x => x.DispatchAttempts).HasColumnName("dispatch_attempts");
            e.Property(x => x.DispatchState).HasColumnName("dispatch_state");
            e.HasIndex(x => new { x.DispatchState, x.CreatedAt });
            e.HasIndex(x => x.CorrelationId);
        });
    }

    private void ApplyPhase3TenantFilters(ModelBuilder modelBuilder)
    {
        ApplyTenantFilter<StudentProfile>(modelBuilder);
        ApplyTenantFilter<StudentSession>(modelBuilder);
        ApplyTenantFilter<LessonViewerState>(modelBuilder);
        ApplyTenantFilter<TutorChatMessage>(modelBuilder);
        ApplyTenantFilter<VoiceCapture>(modelBuilder);
        ApplyTenantFilter<QuizSession>(modelBuilder);
        ApplyTenantFilter<MockTestSession>(modelBuilder);
        ApplyTenantFilter<HomeworkHelpSubmission>(modelBuilder);
        ApplyTenantFilter<WhiteboardSession>(modelBuilder);
        ApplyTenantFilter<SessionEvent>(modelBuilder);
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        Expression<Func<TEntity, bool>> filter = e =>
            _tenantContextAccessor.CurrentTenantId == null ||
            e.TenantId == _tenantContextAccessor.CurrentTenantId;
        modelBuilder.Entity<TEntity>().HasQueryFilter(filter);
    }

    private static void ConfigurePhase4(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProgressRecord>(e =>
        {
            e.ToTable("progress_records");
            e.HasKey(x => x.ProgressRecordId);
            e.Property(x => x.ProgressRecordId).HasColumnName("progress_record_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.SourceEventId).HasColumnName("source_event_id");
            e.Property(x => x.EventKind).HasColumnName("event_kind");
            e.Property(x => x.CurriculumScope).HasColumnName("curriculum_scope").HasColumnType("jsonb");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.IngestedAt).HasColumnName("ingested_at");
            e.HasIndex(x => new { x.TenantId, x.SourceEventId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.StudentId });
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<MasteryState>(e =>
        {
            e.ToTable("mastery_states");
            e.HasKey(x => x.MasteryStateId);
            e.Property(x => x.MasteryStateId).HasColumnName("mastery_state_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.TopicId).HasColumnName("topic_id");
            e.Property(x => x.MasteryScore).HasColumnName("mastery_score").HasColumnType("numeric(6,4)");
            e.Property(x => x.MasteryBand).HasColumnName("mastery_band");
            e.Property(x => x.CalculationVersion).HasColumnName("calculation_version");
            e.Property(x => x.SampleWindowStart).HasColumnName("sample_window_start");
            e.Property(x => x.SampleWindowEnd).HasColumnName("sample_window_end");
            e.Property(x => x.ContributingRecordCount).HasColumnName("contributing_record_count");
            e.Property(x => x.LastUpdatedAt).HasColumnName("last_updated_at");
            e.Property(x => x.LastCorrelationId).HasColumnName("last_correlation_id");
            e.HasIndex(x => new { x.TenantId, x.StudentId, x.SubjectId, x.TopicId, x.CalculationVersion }).IsUnique();
        });

        modelBuilder.Entity<StreakState>(e =>
        {
            e.ToTable("streak_states");
            e.HasKey(x => x.StreakStateId);
            e.Property(x => x.StreakStateId).HasColumnName("streak_state_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.CurrentLength).HasColumnName("current_length");
            e.Property(x => x.LongestLength).HasColumnName("longest_length");
            e.Property(x => x.LastQualifyingDay).HasColumnName("last_qualifying_day").HasColumnType("date");
            e.Property(x => x.FamilyTimezone).HasColumnName("family_timezone");
            e.Property(x => x.ResetHistory).HasColumnName("reset_history").HasColumnType("jsonb");
            e.Property(x => x.LastUpdatedAt).HasColumnName("last_updated_at");
            e.HasIndex(x => new { x.TenantId, x.StudentId }).IsUnique();
        });

        modelBuilder.Entity<BadgeCriterion>(e =>
        {
            e.ToTable("badge_criteria");
            e.HasKey(x => x.BadgeCriterionId);
            e.Property(x => x.BadgeCriterionId).HasColumnName("badge_criterion_id");
            e.Property(x => x.BadgeKey).HasColumnName("badge_key");
            e.Property(x => x.Version).HasColumnName("version");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.DisplayNameAr).HasColumnName("display_name_ar");
            e.Property(x => x.DisplayNameEn).HasColumnName("display_name_en");
            e.Property(x => x.DescriptionAr).HasColumnName("description_ar");
            e.Property(x => x.DescriptionEn).HasColumnName("description_en");
            e.Property(x => x.Threshold).HasColumnName("threshold").HasColumnType("jsonb");
            e.Property(x => x.RetiredAt).HasColumnName("retired_at");
            e.HasIndex(x => new { x.BadgeKey, x.Version }).IsUnique();
        });

        modelBuilder.Entity<BadgeAward>(e =>
        {
            e.ToTable("badge_awards");
            e.HasKey(x => x.BadgeAwardId);
            e.Property(x => x.BadgeAwardId).HasColumnName("badge_award_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.BadgeCriterionId).HasColumnName("badge_criterion_id");
            e.Property(x => x.BadgeCriterionVersion).HasColumnName("badge_criterion_version");
            e.Property(x => x.AwardedAt).HasColumnName("awarded_at");
            e.Property(x => x.OriginatingProgressRecordIds).HasColumnName("originating_progress_record_ids").HasColumnType("jsonb");
            e.Property(x => x.CelebrationShown).HasColumnName("celebration_shown");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.HasIndex(x => new { x.TenantId, x.StudentId, x.BadgeCriterionId, x.BadgeCriterionVersion }).IsUnique();
        });

        modelBuilder.Entity<FocusArea>(e =>
        {
            e.ToTable("focus_areas");
            e.HasKey(x => x.FocusAreaId);
            e.Property(x => x.FocusAreaId).HasColumnName("focus_area_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.ChapterId).HasColumnName("chapter_id");
            e.Property(x => x.TopicId).HasColumnName("topic_id");
            e.Property(x => x.SignalSummary).HasColumnName("signal_summary").HasColumnType("jsonb");
            e.Property(x => x.RationaleAr).HasColumnName("rationale_ar");
            e.Property(x => x.RationaleEn).HasColumnName("rationale_en");
            e.Property(x => x.SuggestedNextStep).HasColumnName("suggested_next_step").HasColumnType("jsonb");
            e.Property(x => x.GuardrailDecisionTrailId).HasColumnName("guardrail_decision_trail_id");
            e.Property(x => x.ComputedAt).HasColumnName("computed_at");
            e.Property(x => x.ValidUntil).HasColumnName("valid_until");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.HasIndex(x => new { x.TenantId, x.StudentId });
        });

        modelBuilder.Entity<GuardrailDecisionTrail>(e =>
        {
            e.ToTable("guardrail_decision_trails");
            e.HasKey(x => x.GuardrailDecisionTrailId);
            e.Property(x => x.GuardrailDecisionTrailId).HasColumnName("guardrail_decision_trail_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ArtefactKind).HasColumnName("artefact_kind");
            e.Property(x => x.ArtefactId).HasColumnName("artefact_id");
            e.Property(x => x.PromptKey).HasColumnName("prompt_key");
            e.Property(x => x.ChainOutput).HasColumnName("chain_output").HasColumnType("jsonb");
            e.Property(x => x.FinalStage).HasColumnName("final_stage");
            e.Property(x => x.Language).HasColumnName("language");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CapturedAt).HasColumnName("captured_at");
            e.HasIndex(x => new { x.ArtefactKind, x.ArtefactId });
        });

        modelBuilder.Entity<WeeklyReport>(e =>
        {
            e.ToTable("weekly_reports");
            e.HasKey(x => x.WeeklyReportId);
            e.Property(x => x.WeeklyReportId).HasColumnName("weekly_report_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.WindowStart).HasColumnName("window_start").HasColumnType("date");
            e.Property(x => x.WindowEnd).HasColumnName("window_end").HasColumnType("date");
            e.Property(x => x.GeneratedAt).HasColumnName("generated_at");
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.MasteryDeltas).HasColumnName("mastery_deltas").HasColumnType("jsonb");
            e.Property(x => x.TopFocusAreas).HasColumnName("top_focus_areas").HasColumnType("jsonb");
            e.Property(x => x.AwardedBadges).HasColumnName("awarded_badges").HasColumnType("jsonb");
            e.Property(x => x.SummaryAr).HasColumnName("summary_ar");
            e.Property(x => x.SummaryEn).HasColumnName("summary_en");
            e.Property(x => x.GuardrailDecisionTrailId).HasColumnName("guardrail_decision_trail_id");
            e.Property(x => x.EvidenceRefs).HasColumnName("evidence_refs").HasColumnType("jsonb");
            e.Property(x => x.ShareTokenHash).HasColumnName("share_token_hash");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.HasIndex(x => new { x.TenantId, x.StudentId, x.WindowStart, x.WindowEnd }).IsUnique();
        });

        modelBuilder.Entity<AtRiskFlag>(e =>
        {
            e.ToTable("at_risk_flags");
            e.HasKey(x => x.AtRiskFlagId);
            e.Property(x => x.AtRiskFlagId).HasColumnName("at_risk_flag_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.ThresholdVersion).HasColumnName("threshold_version");
            e.Property(x => x.TriggeringEvidence).HasColumnName("triggering_evidence").HasColumnType("jsonb");
            e.Property(x => x.RaisedAt).HasColumnName("raised_at");
            e.Property(x => x.ClearedAt).HasColumnName("cleared_at");
            e.Property(x => x.LinkedInterventionPromptId).HasColumnName("linked_intervention_prompt_id");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
            e.Property(x => x.AcknowledgedByParentProfileId).HasColumnName("acknowledged_by_parent_profile_id");
            e.HasIndex(x => new { x.TenantId, x.StudentId, x.ClearedAt });
        });

        modelBuilder.Entity<InterventionPrompt>(e =>
        {
            e.ToTable("intervention_prompts");
            e.HasKey(x => x.InterventionPromptId);
            e.Property(x => x.InterventionPromptId).HasColumnName("intervention_prompt_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.OriginatingFlagId).HasColumnName("originating_flag_id");
            e.Property(x => x.OriginatingFocusAreaId).HasColumnName("originating_focus_area_id");
            e.Property(x => x.BodyAr).HasColumnName("body_ar");
            e.Property(x => x.BodyEn).HasColumnName("body_en");
            e.Property(x => x.NextStep).HasColumnName("next_step").HasColumnType("jsonb");
            e.Property(x => x.GuardrailDecisionTrailId).HasColumnName("guardrail_decision_trail_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.HasIndex(x => new { x.TenantId, x.StudentId, x.CreatedAt });
        });

        modelBuilder.Entity<Phase4DownstreamEvent>(e =>
        {
            e.ToTable("phase4_downstream_events");
            e.HasKey(x => x.Phase4DownstreamEventId);
            e.Property(x => x.Phase4DownstreamEventId).HasColumnName("phase4_downstream_event_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EventKind).HasColumnName("event_kind");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.Scope).HasColumnName("scope").HasColumnType("jsonb");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            e.Property(x => x.DeliveryState).HasColumnName("delivery_state");
            e.Property(x => x.DispatchAttempts).HasColumnName("dispatch_attempts");
            e.HasIndex(x => new { x.DeliveryState, x.OccurredAt });
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<ParentProfile>(e =>
        {
            e.ToTable("parent_profiles");
            e.HasKey(x => x.ParentProfileId);
            e.Property(x => x.ParentProfileId).HasColumnName("parent_profile_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.IdentityId).HasColumnName("identity_id");
            e.Property(x => x.PreferredLanguage).HasColumnName("preferred_language");
            e.Property(x => x.Locale).HasColumnName("locale");
            e.Property(x => x.Timezone).HasColumnName("timezone");
            e.Property(x => x.NotificationChannels).HasColumnName("notification_channels").HasColumnType("jsonb");
            e.Property(x => x.QuietHours).HasColumnName("quiet_hours").HasColumnType("jsonb");
            e.Property(x => x.PerChildOverrides).HasColumnName("per_child_overrides").HasColumnType("jsonb");
            e.Property(x => x.ConsentState).HasColumnName("consent_state").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.TenantId, x.IdentityId }).IsUnique();
        });

        modelBuilder.Entity<ChildLink>(e =>
        {
            e.ToTable("child_links");
            e.HasKey(x => x.ChildLinkId);
            e.Property(x => x.ChildLinkId).HasColumnName("child_link_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ParentProfileId).HasColumnName("parent_profile_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.Role).HasColumnName("role");
            e.Property(x => x.EffectiveStart).HasColumnName("effective_start").HasColumnType("date");
            e.Property(x => x.EffectiveEnd).HasColumnName("effective_end").HasColumnType("date");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.TenantId, x.ParentProfileId, x.StudentId }).IsUnique();
        });

        modelBuilder.Entity<ParentNotification>(e =>
        {
            e.ToTable("parent_notifications");
            e.HasKey(x => x.ParentNotificationId);
            e.Property(x => x.ParentNotificationId).HasColumnName("parent_notification_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ParentProfileId).HasColumnName("parent_profile_id");
            e.Property(x => x.ChildId).HasColumnName("child_id");
            e.Property(x => x.NotificationKind).HasColumnName("notification_kind");
            e.Property(x => x.Channel).HasColumnName("channel");
            e.Property(x => x.Language).HasColumnName("language");
            e.Property(x => x.BodyAr).HasColumnName("body_ar");
            e.Property(x => x.BodyEn).HasColumnName("body_en");
            e.Property(x => x.QuietHoursDeferredUntil).HasColumnName("quiet_hours_deferred_until");
            e.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            e.Property(x => x.DeliveryState).HasColumnName("delivery_state");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.DeliveryState, x.CreatedAt });
        });

        modelBuilder.Entity<OperatorImpersonationAudit>(e =>
        {
            e.ToTable("operator_impersonation_audits");
            e.HasKey(x => x.OperatorImpersonationAuditId);
            e.Property(x => x.OperatorImpersonationAuditId).HasColumnName("operator_impersonation_audit_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OperatorActorId).HasColumnName("operator_actor_id");
            e.Property(x => x.TargetParentProfileId).HasColumnName("target_parent_profile_id");
            e.Property(x => x.TargetChildId).HasColumnName("target_child_id");
            e.Property(x => x.Surface).HasColumnName("surface");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.ViewedAt).HasColumnName("viewed_at");
            e.HasIndex(x => new { x.TenantId, x.TargetParentProfileId, x.ViewedAt });
        });

        modelBuilder.Entity<ProgressIngestionDeadLetter>(e =>
        {
            e.ToTable("progress_ingestion_dead_letters");
            e.HasKey(x => x.DeadLetterId);
            e.Property(x => x.DeadLetterId).HasColumnName("dead_letter_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.SourceEventId).HasColumnName("source_event_id");
            e.Property(x => x.EventKind).HasColumnName("event_kind");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.Envelope).HasColumnName("envelope").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.RecordedAt).HasColumnName("recorded_at");
            e.HasIndex(x => new { x.TenantId, x.RecordedAt });
            e.HasIndex(x => x.Reason);
        });
    }

    private void ApplyPhase4TenantFilters(ModelBuilder modelBuilder)
    {
        ApplyTenantFilter<ProgressRecord>(modelBuilder);
        ApplyTenantFilter<MasteryState>(modelBuilder);
        ApplyTenantFilter<StreakState>(modelBuilder);
        ApplyTenantFilter<BadgeAward>(modelBuilder);
        ApplyTenantFilter<FocusArea>(modelBuilder);
        ApplyTenantFilter<GuardrailDecisionTrail>(modelBuilder);
        ApplyTenantFilter<WeeklyReport>(modelBuilder);
        ApplyTenantFilter<AtRiskFlag>(modelBuilder);
        ApplyTenantFilter<InterventionPrompt>(modelBuilder);
        ApplyTenantFilter<Phase4DownstreamEvent>(modelBuilder);
        ApplyTenantFilter<ParentProfile>(modelBuilder);
        ApplyTenantFilter<ChildLink>(modelBuilder);
        ApplyTenantFilter<ParentNotification>(modelBuilder);
        ApplyTenantFilter<OperatorImpersonationAudit>(modelBuilder);
    }

    private static void ConfigurePhase5(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SchoolTenant>(e =>
        {
            e.ToTable("school_tenants");
            e.HasKey(x => x.SchoolTenantId);
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolNameAr).HasColumnName("school_name_ar");
            e.Property(x => x.SchoolNameEn).HasColumnName("school_name_en");
            e.Property(x => x.CurriculumType).HasColumnName("curriculum_type");
            e.Property(x => x.GradeRangeStart).HasColumnName("grade_range_start");
            e.Property(x => x.GradeRangeEnd).HasColumnName("grade_range_end");
            e.Property(x => x.SubjectBindings).HasColumnName("subject_bindings").HasColumnType("jsonb");
            e.Property(x => x.AcademicCalendar).HasColumnName("academic_calendar").HasColumnType("jsonb");
            e.Property(x => x.PreferredLanguage).HasColumnName("preferred_language");
            e.Property(x => x.SubscriptionStatus).HasColumnName("subscription_status");
            e.Property(x => x.CreatedByOperatorId).HasColumnName("created_by_operator_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<SchoolAdministrator>(e =>
        {
            e.ToTable("school_administrators");
            e.HasKey(x => x.SchoolAdminId);
            e.Property(x => x.SchoolAdminId).HasColumnName("school_admin_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.UserIdentityId).HasColumnName("user_identity_id");
            e.Property(x => x.InvitationEmail).HasColumnName("invitation_email");
            e.Property(x => x.OnboardingStatus).HasColumnName("onboarding_status");
            e.Property(x => x.TermsAcceptedAt).HasColumnName("terms_accepted_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.UserIdentityId }).IsUnique();
        });

        modelBuilder.Entity<Teacher>(e =>
        {
            e.ToTable("teachers");
            e.HasKey(x => x.TeacherId);
            e.Property(x => x.TeacherId).HasColumnName("teacher_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.UserIdentityId).HasColumnName("user_identity_id");
            e.Property(x => x.DisplayNameAr).HasColumnName("display_name_ar");
            e.Property(x => x.DisplayNameEn).HasColumnName("display_name_en");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.UserIdentityId }).IsUnique();
        });

        modelBuilder.Entity<ClassGroup>(e =>
        {
            e.ToTable("class_groups");
            e.HasKey(x => x.ClassGroupId);
            e.Property(x => x.ClassGroupId).HasColumnName("class_group_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.Grade).HasColumnName("grade");
            e.Property(x => x.SectionLabel).HasColumnName("section_label");
            e.Property(x => x.DisplayNameAr).HasColumnName("display_name_ar");
            e.Property(x => x.DisplayNameEn).HasColumnName("display_name_en");
            e.Property(x => x.SubjectBindings).HasColumnName("subject_bindings").HasColumnType("jsonb");
            e.Property(x => x.AcademicYear).HasColumnName("academic_year");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.Grade, x.SectionLabel, x.AcademicYear }).IsUnique();
        });

        modelBuilder.Entity<ClassEnrolment>(e =>
        {
            e.ToTable("class_enrolments");
            e.HasKey(x => x.ClassEnrolmentId);
            e.Property(x => x.ClassEnrolmentId).HasColumnName("class_enrolment_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ClassGroupId).HasColumnName("class_group_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.EnrolledAt).HasColumnName("enrolled_at");
            e.Property(x => x.UnenrolledAt).HasColumnName("unenrolled_at");
            e.Property(x => x.TransferToClassId).HasColumnName("transfer_to_class_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.HasIndex(x => new { x.ClassGroupId, x.StudentId, x.Status });
        });

        modelBuilder.Entity<TeacherAssignment>(e =>
        {
            e.ToTable("teacher_assignments");
            e.HasKey(x => x.TeacherAssignmentId);
            e.Property(x => x.TeacherAssignmentId).HasColumnName("teacher_assignment_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.TeacherId).HasColumnName("teacher_id");
            e.Property(x => x.ClassGroupId).HasColumnName("class_group_id");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.AssignedAt).HasColumnName("assigned_at");
            e.Property(x => x.UnassignedAt).HasColumnName("unassigned_at");
            e.HasIndex(x => new { x.TeacherId, x.ClassGroupId, x.SubjectId });
        });

        modelBuilder.Entity<RosterImport>(e =>
        {
            e.ToTable("roster_imports");
            e.HasKey(x => x.RosterImportId);
            e.Property(x => x.RosterImportId).HasColumnName("roster_import_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.UploadedByAdminId).HasColumnName("uploaded_by_admin_id");
            e.Property(x => x.SourceFileBlobKey).HasColumnName("source_file_blob_key");
            e.Property(x => x.OriginalFileName).HasColumnName("original_file_name");
            e.Property(x => x.TotalRowCount).HasColumnName("total_row_count");
            e.Property(x => x.SuccessCount).HasColumnName("success_count");
            e.Property(x => x.ErrorCount).HasColumnName("error_count");
            e.Property(x => x.SkipCount).HasColumnName("skip_count");
            e.Property(x => x.ErrorReportBlobKey).HasColumnName("error_report_blob_key");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.CreatedAt });
        });

        modelBuilder.Entity<Exam>(e =>
        {
            e.ToTable("exams");
            e.HasKey(x => x.ExamId);
            e.Property(x => x.ExamId).HasColumnName("exam_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.CreatedByTeacherId).HasColumnName("created_by_teacher_id");
            e.Property(x => x.CreatedByAdminId).HasColumnName("created_by_admin_id");
            e.Property(x => x.TitleAr).HasColumnName("title_ar");
            e.Property(x => x.TitleEn).HasColumnName("title_en");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.Grade).HasColumnName("grade");
            e.Property(x => x.TopicBindings).HasColumnName("topic_bindings").HasColumnType("jsonb");
            e.Property(x => x.ScheduledStart).HasColumnName("scheduled_start");
            e.Property(x => x.ScheduledEnd).HasColumnName("scheduled_end");
            e.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.TotalPoints).HasColumnName("total_points").HasColumnType("numeric(9,2)");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.Status });
        });

        modelBuilder.Entity<ExamQuestion>(e =>
        {
            e.ToTable("exam_questions");
            e.HasKey(x => x.ExamQuestionId);
            e.Property(x => x.ExamQuestionId).HasColumnName("exam_question_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ExamId).HasColumnName("exam_id");
            e.Property(x => x.QuestionSource).HasColumnName("question_source");
            e.Property(x => x.Phase1ContentId).HasColumnName("phase1_content_id");
            e.Property(x => x.QuestionTextAr).HasColumnName("question_text_ar");
            e.Property(x => x.QuestionTextEn).HasColumnName("question_text_en");
            e.Property(x => x.QuestionType).HasColumnName("question_type");
            e.Property(x => x.Options).HasColumnName("options").HasColumnType("jsonb");
            e.Property(x => x.CorrectAnswer).HasColumnName("correct_answer").HasColumnType("jsonb");
            e.Property(x => x.Points).HasColumnName("points").HasColumnType("numeric(6,2)");
            e.Property(x => x.DisplayOrder).HasColumnName("display_order");
            e.Property(x => x.GuardrailDecisionTrailId).HasColumnName("guardrail_decision_trail_id");
            e.HasIndex(x => new { x.ExamId, x.DisplayOrder });
        });

        modelBuilder.Entity<ExamAssignment>(e =>
        {
            e.ToTable("exam_assignments");
            e.HasKey(x => x.ExamAssignmentId);
            e.Property(x => x.ExamAssignmentId).HasColumnName("exam_assignment_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ExamId).HasColumnName("exam_id");
            e.Property(x => x.ClassGroupId).HasColumnName("class_group_id");
            e.Property(x => x.AssignedAt).HasColumnName("assigned_at");
            e.HasIndex(x => new { x.ExamId, x.ClassGroupId }).IsUnique();
        });

        modelBuilder.Entity<ExamSubmission>(e =>
        {
            e.ToTable("exam_submissions");
            e.HasKey(x => x.ExamSubmissionId);
            e.Property(x => x.ExamSubmissionId).HasColumnName("exam_submission_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ExamId).HasColumnName("exam_id");
            e.Property(x => x.StudentId).HasColumnName("student_id");
            e.Property(x => x.Answers).HasColumnName("answers").HasColumnType("jsonb");
            e.Property(x => x.Score).HasColumnName("score").HasColumnType("numeric(9,2)");
            e.Property(x => x.MaxScore).HasColumnName("max_score").HasColumnType("numeric(9,2)");
            e.Property(x => x.GradingStatus).HasColumnName("grading_status");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.SubmittedAt).HasColumnName("submitted_at");
            e.Property(x => x.GradedAt).HasColumnName("graded_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.HasIndex(x => new { x.ExamId, x.StudentId }).IsUnique();
        });

        modelBuilder.Entity<LeaderboardSnapshot>(e =>
        {
            e.ToTable("leaderboard_snapshots");
            e.HasKey(x => x.LeaderboardSnapshotId);
            e.Property(x => x.LeaderboardSnapshotId).HasColumnName("leaderboard_snapshot_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.ScopeType).HasColumnName("scope_type");
            e.Property(x => x.ScopeId).HasColumnName("scope_id");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.Metric).HasColumnName("metric");
            e.Property(x => x.WindowStart).HasColumnName("window_start");
            e.Property(x => x.WindowEnd).HasColumnName("window_end");
            e.Property(x => x.Entries).HasColumnName("entries").HasColumnType("jsonb");
            e.Property(x => x.PrivacyMode).HasColumnName("privacy_mode");
            e.Property(x => x.ComputedAt).HasColumnName("computed_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.ScopeType, x.ScopeId, x.Metric, x.ComputedAt });
        });

        modelBuilder.Entity<LeaderboardConfig>(e =>
        {
            e.ToTable("leaderboard_configs");
            e.HasKey(x => x.LeaderboardConfigId);
            e.Property(x => x.LeaderboardConfigId).HasColumnName("leaderboard_config_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.PrivacyMode).HasColumnName("privacy_mode");
            e.Property(x => x.LeaderboardEnabled).HasColumnName("leaderboard_enabled");
            e.Property(x => x.PerClassOverridesJson).HasColumnName("per_class_overrides").HasColumnType("jsonb");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.SchoolTenantId }).IsUnique();
        });

        modelBuilder.Entity<Announcement>(e =>
        {
            e.ToTable("announcements");
            e.HasKey(x => x.AnnouncementId);
            e.Property(x => x.AnnouncementId).HasColumnName("announcement_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.CreatedById).HasColumnName("created_by_id");
            e.Property(x => x.TargetScope).HasColumnName("target_scope");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.TargetGrade).HasColumnName("target_grade");
            e.Property(x => x.TitleAr).HasColumnName("title_ar");
            e.Property(x => x.TitleEn).HasColumnName("title_en");
            e.Property(x => x.BodyAr).HasColumnName("body_ar");
            e.Property(x => x.BodyEn).HasColumnName("body_en");
            e.Property(x => x.Attachments).HasColumnName("attachments").HasColumnType("jsonb");
            e.Property(x => x.ScheduledPublishAt).HasColumnName("scheduled_publish_at");
            e.Property(x => x.PublishedAt).HasColumnName("published_at");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.Status });
        });

        modelBuilder.Entity<AnnouncementDelivery>(e =>
        {
            e.ToTable("announcement_deliveries");
            e.HasKey(x => x.AnnouncementDeliveryId);
            e.Property(x => x.AnnouncementDeliveryId).HasColumnName("announcement_delivery_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.AnnouncementId).HasColumnName("announcement_id");
            e.Property(x => x.RecipientId).HasColumnName("recipient_id");
            e.Property(x => x.RecipientRole).HasColumnName("recipient_role");
            e.Property(x => x.Channel).HasColumnName("channel");
            e.Property(x => x.DeliveryStatus).HasColumnName("delivery_status");
            e.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
            e.Property(x => x.ReadAt).HasColumnName("read_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.HasIndex(x => new { x.AnnouncementId, x.RecipientId });
        });

        modelBuilder.Entity<SchoolReport>(e =>
        {
            e.ToTable("school_reports");
            e.HasKey(x => x.SchoolReportId);
            e.Property(x => x.SchoolReportId).HasColumnName("school_report_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.GeneratedByAdminId).HasColumnName("generated_by_admin_id");
            e.Property(x => x.ReportType).HasColumnName("report_type");
            e.Property(x => x.GradeFilter).HasColumnName("grade_filter");
            e.Property(x => x.SubjectFilter).HasColumnName("subject_filter");
            e.Property(x => x.ClassFilter).HasColumnName("class_filter");
            e.Property(x => x.WindowStart).HasColumnName("window_start");
            e.Property(x => x.WindowEnd).HasColumnName("window_end");
            e.Property(x => x.Language).HasColumnName("language");
            e.Property(x => x.ExportBlobKey).HasColumnName("export_blob_key");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(x => new { x.SchoolTenantId, x.CreatedAt });
        });

        modelBuilder.Entity<SchoolLicense>(e =>
        {
            e.ToTable("school_licenses");
            e.HasKey(x => x.SchoolLicenseId);
            e.Property(x => x.SchoolLicenseId).HasColumnName("school_license_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.PlanTier).HasColumnName("plan_tier");
            e.Property(x => x.SeatLimit).HasColumnName("seat_limit");
            e.Property(x => x.SeatsUsed).HasColumnName("seats_used");
            e.Property(x => x.FeatureGates).HasColumnName("feature_gates").HasColumnType("jsonb");
            e.Property(x => x.SubscriptionStart).HasColumnName("subscription_start");
            e.Property(x => x.SubscriptionEnd).HasColumnName("subscription_end");
            e.Property(x => x.IsTrial).HasColumnName("is_trial");
            e.Property(x => x.SeatWarningThreshold).HasColumnName("seat_warning_threshold");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.SchoolTenantId).IsUnique();
        });

        modelBuilder.Entity<SchoolAggregateView>(e =>
        {
            e.ToTable("school_aggregate_views");
            e.HasKey(x => x.AggregateViewId);
            e.Property(x => x.AggregateViewId).HasColumnName("aggregate_view_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.ScopeType).HasColumnName("scope_type");
            e.Property(x => x.ScopeId).HasColumnName("scope_id");
            e.Property(x => x.Grade).HasColumnName("grade");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.ActiveStudentCount).HasColumnName("active_student_count");
            e.Property(x => x.AverageMastery).HasColumnName("average_mastery").HasColumnType("numeric(6,4)");
            e.Property(x => x.AtRiskCount).HasColumnName("at_risk_count");
            e.Property(x => x.ActiveStreakCount).HasColumnName("active_streak_count");
            e.Property(x => x.BadgesAwardedCount).HasColumnName("badges_awarded_count");
            e.Property(x => x.LastUpdatedAt).HasColumnName("last_updated_at");
            e.Property(x => x.LastEventId).HasColumnName("last_event_id");
            e.HasIndex(x => new { x.SchoolTenantId, x.ScopeType, x.ScopeId, x.SubjectId }).IsUnique();
        });

        modelBuilder.Entity<Phase5DownstreamEvent>(e =>
        {
            e.ToTable("phase5_downstream_events");
            e.HasKey(x => x.Phase5EventId);
            e.Property(x => x.Phase5EventId).HasColumnName("phase5_event_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.SchoolTenantId).HasColumnName("school_tenant_id");
            e.Property(x => x.EventKind).HasColumnName("event_kind");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            e.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            e.Property(x => x.DeliveryState).HasColumnName("delivery_state");
            e.Property(x => x.DispatchAttempts).HasColumnName("dispatch_attempts");
            e.HasIndex(x => new { x.DeliveryState, x.OccurredAt });
            e.HasIndex(x => x.CorrelationId);
        });
    }

    private static void ConfigurePhase6(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SubscriptionPlan>(e =>
        {
            e.ToTable("subscription_plans");
            e.HasKey(x => x.PlanId);
            e.Property(x => x.PlanId).HasColumnName("plan_id");
            e.Property(x => x.PlanNameAr).HasColumnName("plan_name_ar");
            e.Property(x => x.PlanNameEn).HasColumnName("plan_name_en");
            e.Property(x => x.PlanType).HasColumnName("plan_type");
            e.Property(x => x.Tier).HasColumnName("tier");
            e.Property(x => x.PriceEgp).HasColumnName("price_egp").HasColumnType("decimal(10,2)");
            e.Property(x => x.PriceUsd).HasColumnName("price_usd").HasColumnType("decimal(10,2)");
            e.Property(x => x.BillingCycle).HasColumnName("billing_cycle");
            e.Property(x => x.SeatLimit).HasColumnName("seat_limit");
            e.Property(x => x.FeatureEntitlements).HasColumnName("feature_entitlements").HasColumnType("jsonb");
            e.Property(x => x.UsageLimits).HasColumnName("usage_limits").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedByOperatorId).HasColumnName("created_by_operator_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.PlanType, x.Tier, x.BillingCycle }).IsUnique();
        });

        modelBuilder.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.SubscriptionId);
            e.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PlanId).HasColumnName("plan_id");
            e.Property(x => x.PlanType).HasColumnName("plan_type");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CurrentPeriodStart).HasColumnName("current_period_start");
            e.Property(x => x.CurrentPeriodEnd).HasColumnName("current_period_end");
            e.Property(x => x.TrialEnd).HasColumnName("trial_end");
            e.Property(x => x.GracePeriodEnd).HasColumnName("grace_period_end");
            e.Property(x => x.PaymentMethodRef).HasColumnName("payment_method_ref")
                .HasConversion(new EncryptedStringConverter());
            e.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
            e.Property(x => x.CancellationReason).HasColumnName("cancellation_reason");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<Invoice>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.InvoiceId);
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.InvoiceNumber).HasColumnName("invoice_number");
            e.Property(x => x.PeriodStart).HasColumnName("period_start");
            e.Property(x => x.PeriodEnd).HasColumnName("period_end");
            e.Property(x => x.LineItems).HasColumnName("line_items").HasColumnType("jsonb");
            e.Property(x => x.Subtotal).HasColumnName("subtotal").HasColumnType("decimal(10,2)");
            e.Property(x => x.TaxAmount).HasColumnName("tax_amount").HasColumnType("decimal(10,2)");
            e.Property(x => x.Total).HasColumnName("total").HasColumnType("decimal(10,2)");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.PaymentStatus).HasColumnName("payment_status");
            e.Property(x => x.PdfBlobKey).HasColumnName("pdf_blob_key");
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.PaidAt).HasColumnName("paid_at");
            e.HasIndex(x => x.InvoiceNumber).IsUnique();
        });

        modelBuilder.Entity<PaymentTransaction>(e =>
        {
            e.ToTable("payment_transactions");
            e.HasKey(x => x.TransactionId);
            e.Property(x => x.TransactionId).HasColumnName("transaction_id");
            e.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            e.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ProviderName).HasColumnName("provider_name");
            e.Property(x => x.ProviderReference).HasColumnName("provider_reference");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("decimal(10,2)");
            e.Property(x => x.Currency).HasColumnName("currency");
            e.Property(x => x.TransactionType).HasColumnName("transaction_type");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.FailureReason).HasColumnName("failure_reason");
            e.Property(x => x.FailureCode).HasColumnName("failure_code");
            e.Property(x => x.WebhookPayload).HasColumnName("webhook_payload").HasColumnType("jsonb")
                .HasConversion(new EncryptedJsonConverter());
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.AttemptedAt).HasColumnName("attempted_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(x => new { x.ProviderReference, x.TransactionType }).IsUnique()
                .HasFilter("provider_reference IS NOT NULL");
            e.HasIndex(x => x.IdempotencyKey);
        });

        modelBuilder.Entity<NotificationProviderBinding>(e =>
        {
            e.ToTable("notification_provider_bindings");
            e.HasKey(x => x.BindingId);
            e.Property(x => x.BindingId).HasColumnName("binding_id");
            e.Property(x => x.Channel).HasColumnName("channel");
            e.Property(x => x.ProviderName).HasColumnName("provider_name");
            e.Property(x => x.Environment).HasColumnName("environment");
            e.Property(x => x.Configuration).HasColumnName("configuration").HasColumnType("jsonb")
                .HasConversion(new EncryptedJsonConverterNonNull());
            e.Property(x => x.RateLimitPerMinute).HasColumnName("rate_limit_per_minute");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.Channel, x.Environment }).IsUnique();
        });

        modelBuilder.Entity<NotificationDeliveryReceipt>(e =>
        {
            e.ToTable("notification_delivery_receipts");
            e.HasKey(x => x.ReceiptId);
            e.Property(x => x.ReceiptId).HasColumnName("receipt_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.NotificationId).HasColumnName("notification_id");
            e.Property(x => x.RecipientId).HasColumnName("recipient_id");
            e.Property(x => x.Channel).HasColumnName("channel");
            e.Property(x => x.ProviderName).HasColumnName("provider_name");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id");
            e.Property(x => x.FailureReason).HasColumnName("failure_reason");
            e.Property(x => x.RetryCount).HasColumnName("retry_count");
            e.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            e.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
            e.HasIndex(x => new { x.NotificationId, x.RecipientId, x.Channel });
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<AIOperationsMetric>(e =>
        {
            e.ToTable("phase6_ai_operations_metrics");
            e.HasKey(x => x.MetricId);
            e.Property(x => x.MetricId).HasColumnName("metric_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Phase).HasColumnName("phase");
            e.Property(x => x.PromptKey).HasColumnName("prompt_key");
            e.Property(x => x.PromptVersion).HasColumnName("prompt_version");
            e.Property(x => x.ProviderName).HasColumnName("provider_name");
            e.Property(x => x.RequestCount).HasColumnName("request_count");
            e.Property(x => x.TotalInputTokens).HasColumnName("total_input_tokens");
            e.Property(x => x.TotalOutputTokens).HasColumnName("total_output_tokens");
            e.Property(x => x.EstimatedCostEgp).HasColumnName("estimated_cost_egp").HasColumnType("decimal(10,4)");
            e.Property(x => x.LatencyMs).HasColumnName("latency_ms");
            e.Property(x => x.GuardrailOutcome).HasColumnName("guardrail_outcome");
            e.Property(x => x.ConfidenceScore).HasColumnName("confidence_score").HasColumnType("decimal(3,2)");
            e.Property(x => x.WasRefusal).HasColumnName("was_refusal");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasIndex(x => new { x.Phase, x.OccurredAt });
            e.HasIndex(x => new { x.PromptKey, x.OccurredAt });
            e.HasIndex(x => new { x.GuardrailOutcome, x.OccurredAt });
        });

        modelBuilder.Entity<AIOperationsAggregate>(e =>
        {
            e.ToTable("ai_operations_aggregates");
            e.HasKey(x => x.AggregateId);
            e.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Phase).HasColumnName("phase");
            e.Property(x => x.PromptKey).HasColumnName("prompt_key");
            e.Property(x => x.PeriodType).HasColumnName("period_type");
            e.Property(x => x.PeriodStart).HasColumnName("period_start");
            e.Property(x => x.RequestCount).HasColumnName("request_count");
            e.Property(x => x.TotalInputTokens).HasColumnName("total_input_tokens");
            e.Property(x => x.TotalOutputTokens).HasColumnName("total_output_tokens");
            e.Property(x => x.TotalCostEgp).HasColumnName("total_cost_egp").HasColumnType("decimal(12,4)");
            e.Property(x => x.AvgLatencyMs).HasColumnName("avg_latency_ms");
            e.Property(x => x.P95LatencyMs).HasColumnName("p95_latency_ms");
            e.Property(x => x.P99LatencyMs).HasColumnName("p99_latency_ms");
            e.Property(x => x.GuardrailPassCount).HasColumnName("guardrail_pass_count");
            e.Property(x => x.GuardrailWarnCount).HasColumnName("guardrail_warn_count");
            e.Property(x => x.GuardrailBlockCount).HasColumnName("guardrail_block_count");
            e.Property(x => x.RefusalCount).HasColumnName("refusal_count");
            e.Property(x => x.ComputedAt).HasColumnName("computed_at");
            e.HasIndex(x => new { x.TenantId, x.Phase, x.PromptKey, x.PeriodType, x.PeriodStart }).IsUnique();
        });

        modelBuilder.Entity<AlertRule>(e =>
        {
            e.ToTable("alert_rules");
            e.HasKey(x => x.RuleId);
            e.Property(x => x.RuleId).HasColumnName("rule_id");
            e.Property(x => x.RuleName).HasColumnName("rule_name");
            e.Property(x => x.MetricType).HasColumnName("metric_type");
            e.Property(x => x.ThresholdValue).HasColumnName("threshold_value").HasColumnType("decimal(12,4)");
            e.Property(x => x.ThresholdDirection).HasColumnName("threshold_direction");
            e.Property(x => x.EvaluationWindowMin).HasColumnName("evaluation_window_min");
            e.Property(x => x.CooldownMin).HasColumnName("cooldown_min");
            e.Property(x => x.TenantScope).HasColumnName("tenant_scope");
            e.Property(x => x.NotificationTargets).HasColumnName("notification_targets").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedByOperatorId).HasColumnName("created_by_operator_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<AlertEvent>(e =>
        {
            e.ToTable("alert_events");
            e.HasKey(x => x.AlertEventId);
            e.Property(x => x.AlertEventId).HasColumnName("alert_event_id");
            e.Property(x => x.RuleId).HasColumnName("rule_id");
            e.Property(x => x.TriggeringValue).HasColumnName("triggering_value").HasColumnType("decimal(12,4)");
            e.Property(x => x.ThresholdValue).HasColumnName("threshold_value").HasColumnType("decimal(12,4)");
            e.Property(x => x.AffectedTenants).HasColumnName("affected_tenants").HasColumnType("jsonb");
            e.Property(x => x.SampleCorrelationIds).HasColumnName("sample_correlation_ids").HasColumnType("jsonb");
            e.Property(x => x.ResolutionStatus).HasColumnName("resolution_status");
            e.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.ResolutionNotes).HasColumnName("resolution_notes");
            e.Property(x => x.FiredAt).HasColumnName("fired_at");
            e.HasIndex(x => new { x.RuleId, x.FiredAt });
        });

        modelBuilder.Entity<IncidentRecord>(e =>
        {
            e.ToTable("incident_records");
            e.HasKey(x => x.IncidentId);
            e.Property(x => x.IncidentId).HasColumnName("incident_id");
            e.Property(x => x.Severity).HasColumnName("severity");
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.AffectedServices).HasColumnName("affected_services").HasColumnType("jsonb");
            e.Property(x => x.AffectedTenants).HasColumnName("affected_tenants").HasColumnType("jsonb");
            e.Property(x => x.RootCause).HasColumnName("root_cause");
            e.Property(x => x.Resolution).HasColumnName("resolution");
            e.Property(x => x.RunbookReference).HasColumnName("runbook_reference");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.OpenedBy).HasColumnName("opened_by");
            e.Property(x => x.OpenedAt).HasColumnName("opened_at");
            e.Property(x => x.MitigatedAt).HasColumnName("mitigated_at");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.Timeline).HasColumnName("timeline").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            // T079 — indexes for list filters (status/severity) and correlation lookup
            e.HasIndex(x => new { x.Status, x.OpenedAt });
            e.HasIndex(x => new { x.Severity, x.OpenedAt });
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entries");
            e.HasKey(x => x.AuditEntryId);
            e.Property(x => x.AuditEntryId).HasColumnName("audit_entry_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.ActorType).HasColumnName("actor_type");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.TargetType).HasColumnName("target_type");
            e.Property(x => x.ActionType).HasColumnName("action_type");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.IpAddress).HasColumnName("ip_address");
            e.Property(x => x.UserAgent).HasColumnName("user_agent");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasIndex(x => new { x.ActorId, x.OccurredAt });
            e.HasIndex(x => new { x.TargetId, x.OccurredAt });
            e.HasIndex(x => new { x.ActionType, x.OccurredAt });
            e.HasIndex(x => x.CorrelationId);
        });

        modelBuilder.Entity<DataDeletionRequest>(e =>
        {
            e.ToTable("data_deletion_requests");
            e.HasKey(x => x.DeletionRequestId);
            e.Property(x => x.DeletionRequestId).HasColumnName("deletion_request_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.TargetScope).HasColumnName("target_scope");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.RequestedBy).HasColumnName("requested_by");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.TablesProcessed).HasColumnName("tables_processed").HasColumnType("jsonb");
            e.Property(x => x.ErrorDetails).HasColumnName("error_details");
            e.Property(x => x.RequestedAt).HasColumnName("requested_at");
            e.Property(x => x.ProcessingStartedAt).HasColumnName("processing_started_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.ConfirmationSentAt).HasColumnName("confirmation_sent_at");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        });

        modelBuilder.Entity<DataRetentionPolicy>(e =>
        {
            e.ToTable("data_retention_policies");
            e.HasKey(x => x.PolicyId);
            e.Property(x => x.PolicyId).HasColumnName("policy_id");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.RetentionDays).HasColumnName("retention_days");
            e.Property(x => x.AnonymisationRule).HasColumnName("anonymisation_rule");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.LastExecutedAt).HasColumnName("last_executed_at");
            e.Property(x => x.RowsAffectedLastRun).HasColumnName("rows_affected_last_run");
            e.Property(x => x.CreatedByOperatorId).HasColumnName("created_by_operator_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => x.EntityType).IsUnique();
        });

        modelBuilder.Entity<FeatureFlag>(e =>
        {
            e.ToTable("feature_flags");
            e.HasKey(x => x.FeatureFlagId);
            e.Property(x => x.FeatureFlagId).HasColumnName("feature_flag_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.FlagName).HasColumnName("flag_name");
            e.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            e.Property(x => x.ChangedByOperatorId).HasColumnName("changed_by_operator_id");
            e.Property(x => x.ChangedAt).HasColumnName("changed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.TenantId, x.FlagName }).IsUnique();
        });

        modelBuilder.Entity<LaunchReadinessGate>(e =>
        {
            e.ToTable("launch_readiness_gates");
            e.HasKey(x => x.GateId);
            e.Property(x => x.GateId).HasColumnName("gate_id");
            e.Property(x => x.EvaluationName).HasColumnName("evaluation_name");
            e.Property(x => x.CriteriaResults).HasColumnName("criteria_results").HasColumnType("jsonb");
            e.Property(x => x.OverallStatus).HasColumnName("overall_status");
            e.Property(x => x.EvaluatedBy).HasColumnName("evaluated_by");
            e.Property(x => x.EvaluatedAt).HasColumnName("evaluated_at");
        });

        modelBuilder.Entity<TenantHealthView>(e =>
        {
            e.ToTable("tenant_health_views");
            e.HasKey(x => x.TenantHealthId);
            e.Property(x => x.TenantHealthId).HasColumnName("tenant_health_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.TenantType).HasColumnName("tenant_type");
            e.Property(x => x.SubscriptionStatus).HasColumnName("subscription_status");
            e.Property(x => x.PlanTier).HasColumnName("plan_tier");
            e.Property(x => x.ActiveStudentCount).HasColumnName("active_student_count");
            e.Property(x => x.MonthlySessionCount).HasColumnName("monthly_session_count");
            e.Property(x => x.MonthlyAiCostEgp).HasColumnName("monthly_ai_cost_egp").HasColumnType("decimal(10,4)");
            e.Property(x => x.StorageUsageMb).HasColumnName("storage_usage_mb");
            e.Property(x => x.EngagementScore).HasColumnName("engagement_score").HasColumnType("decimal(3,2)");
            e.Property(x => x.AtRiskStudentCount).HasColumnName("at_risk_student_count");
            e.Property(x => x.LastActivityAt).HasColumnName("last_activity_at");
            e.Property(x => x.ComputedAt).HasColumnName("computed_at");
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<Phase6OperationalEvent>(e =>
        {
            e.ToTable("phase6_operational_events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EventKind).HasColumnName("event_kind");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.DispatchedAt).HasColumnName("dispatched_at");
            e.Property(x => x.SchemaVersion).HasColumnName("schema_version");
            e.HasIndex(x => new { x.DispatchedAt, x.OccurredAt });
            e.HasIndex(x => x.CorrelationId);
        });
    }

    private void ApplyPhase6TenantFilters(ModelBuilder modelBuilder)
    {
        ApplyTenantFilter<Subscription>(modelBuilder);
        ApplyTenantFilter<Invoice>(modelBuilder);
        ApplyTenantFilter<PaymentTransaction>(modelBuilder);
        ApplyTenantFilter<NotificationDeliveryReceipt>(modelBuilder);
        ApplyTenantFilter<AIOperationsMetric>(modelBuilder);
        ApplyTenantFilter<AuditEntry>(modelBuilder);
        ApplyTenantFilter<DataDeletionRequest>(modelBuilder);
        ApplyTenantFilter<FeatureFlag>(modelBuilder);
    }

    private void ApplyPhase5TenantFilters(ModelBuilder modelBuilder)
    {
        ApplyTenantFilter<SchoolTenant>(modelBuilder);
        ApplyTenantFilter<SchoolAdministrator>(modelBuilder);
        ApplyTenantFilter<Teacher>(modelBuilder);
        ApplyTenantFilter<ClassGroup>(modelBuilder);
        ApplyTenantFilter<ClassEnrolment>(modelBuilder);
        ApplyTenantFilter<TeacherAssignment>(modelBuilder);
        ApplyTenantFilter<RosterImport>(modelBuilder);
        ApplyTenantFilter<Exam>(modelBuilder);
        ApplyTenantFilter<ExamQuestion>(modelBuilder);
        ApplyTenantFilter<ExamAssignment>(modelBuilder);
        ApplyTenantFilter<ExamSubmission>(modelBuilder);
        ApplyTenantFilter<LeaderboardSnapshot>(modelBuilder);
        ApplyTenantFilter<LeaderboardConfig>(modelBuilder);
        ApplyTenantFilter<Announcement>(modelBuilder);
        ApplyTenantFilter<AnnouncementDelivery>(modelBuilder);
        ApplyTenantFilter<SchoolReport>(modelBuilder);
        ApplyTenantFilter<SchoolLicense>(modelBuilder);
        ApplyTenantFilter<SchoolAggregateView>(modelBuilder);
        ApplyTenantFilter<Phase5DownstreamEvent>(modelBuilder);
    }
}
