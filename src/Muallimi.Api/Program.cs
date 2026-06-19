using Muallimi.Api.AiOperations;
using Muallimi.Api.AiOperations.AlertRuleEngine;
using Muallimi.Api.AiOperations.MetricAggregation;
using Muallimi.Api.Billing;
using Muallimi.Api.Notifications.DeliveryTracking;
using Muallimi.Api.Notifications.ProductionProviderBindings;
using Muallimi.Api.Notifications.RetryAndDeadLetter;
using Muallimi.Api.Audit;
using Muallimi.Api.Coverage;
using Muallimi.Api.Curriculum;
using Muallimi.Api.Engagement.AtRiskDetection;
using Muallimi.Api.Engagement.BadgeAwarding;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.FocusAreas;
using Muallimi.Api.Engagement.InterventionPrompts;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.Observability;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Api.Engagement.StreakCalculation;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Api.Engagement;
using Muallimi.Api.Exams;
using Muallimi.Api.Exams.ExamAdministration;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Exams.ExamResults;
using Muallimi.Api.Announcements;
using Muallimi.Api.Announcements.AnnouncementCreation;
using Muallimi.Api.Announcements.AnnouncementDispatch;
using Muallimi.Api.Leaderboards;
using Muallimi.Api.Leaderboards.LeaderboardComputation;
using Muallimi.Api.Leaderboards.LeaderboardQuery;
using Muallimi.Api.StudentProgressSurface;
using Muallimi.Api.Parents;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Api.Parents.ParentNotifications.Channels;
using Muallimi.Api.SchoolManagement;
using Muallimi.Api.SchoolManagement.AdminOnboarding;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.Licensing;
using Muallimi.Api.SchoolManagement.Phase4EventConsumer;
using Muallimi.Api.SchoolManagement.RosterImport;
using Muallimi.Api.SchoolManagement.SchoolDashboard;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Api.SchoolManagement.TeacherDashboard;
using Muallimi.Api.SchoolReports;
using Muallimi.Api.SchoolReports.ReportAggregation;
using Muallimi.Api.SchoolReports.ReportExport;
using Muallimi.Api.PromptAudit;
using Muallimi.Api.ProviderBindings;
using Muallimi.Api.Publication;
using Muallimi.Api.RetrievalApi;
using Muallimi.Api.StudentExperience;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.HomeworkHelp;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Api.StudentExperience.MockTest;
using Muallimi.Api.StudentExperience.PlanGating;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Api.StudentExperience.Tenancy;
using Muallimi.Api.StudentExperience.TutorExposure;
using Muallimi.Api.StudentExperience.Whiteboard;
using Muallimi.Api.TutorExposure;
using Muallimi.Api.Billing.EntitlementEnforcement;
using Muallimi.Api.Observability.DistributedTracing;
using Muallimi.Api.Observability.HealthChecks;
using Muallimi.Api.AiOperations.IncidentManagement;
using Muallimi.Api.Identity.Middleware;
using Muallimi.Api.Identity.Startup;
using Muallimi.Infrastructure.Identity.Seed;
using Muallimi.Application.Audit;
using Muallimi.Infrastructure.AiOperations;
using Muallimi.Infrastructure.BlobStorage;
using Muallimi.Infrastructure.Persistence;
using Muallimi.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Minio;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// JSON wire format:
// Phase 3+ contracts (student / parent / teacher / school-admin / operator / billing etc.)
// use snake_case everywhere — request bodies AND response payloads — per the
// contract markdown files. Auth / Identity DTOs use camelCase and PIN that via
// explicit [JsonPropertyName] attributes on every property (verified 100%
// coverage in Muallimi.Application/Identity/Dtos). Attributes override the
// policy, so flipping the global default to snake_case works without touching
// the Identity module.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Serilog
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.With<Muallimi.Api.Security.PIIMasking.PIIMaskingEnricher>()
    .Enrich.WithProperty("service_name", "main-backend")
    .WriteTo.Console()
    .WriteTo.Seq(context.Configuration["Seq:Url"] ?? "http://localhost:5341"));

// EF Core + PostgreSQL + pgvector
// Phase 3 (T007): register ambient tenant accessor so the DbContext's global
// query filters scope every tenant-aware query by X-Tenant-Id.
builder.Services.AddPhase3Tenancy();
builder.Services.AddPhase3CorrelationPropagation();
builder.Services.AddDbContext<MuallimiDbContext>((sp, options) =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=muallimi_dev;Username=muallimi;Password=muallimi",
        npgsql => npgsql.UseVector()));
// The DbContext's tenant-aware constructor takes IDbTenantContextAccessor via
// DI; AddDbContext picks the longest ctor that it can resolve, so the
// accessor registered above is injected automatically.

// Application services
builder.Services.AddSingleton<AuditEventEmitter>();

// US4: Runtime retrieval services
builder.Services.AddScoped<ChunkRetrievalService>();
builder.Services.AddScoped<QaCacheLookupService>();
builder.Services.AddSingleton<LogicalCdnUrlProvider>();

// US5: Asset invalidation handler + fallback mode projection
builder.Services.AddScoped<AssetInvalidationHandler>();
builder.Services.AddScoped<FallbackModeProjection>();

// US5: Deprecated chunk cleanup worker (30-day grace period)
builder.Services.AddHostedService<Muallimi.Api.Curriculum.DeprecatedChunkCleanupWorker>();

// US6: Coverage dashboard aggregator
builder.Services.AddScoped<CoverageAggregator>();

// ── Phase 2 US1 (T035–T036): Tutor exposure facade + decision record persistence ──
builder.Services.AddTutorExposureClient(builder.Configuration);
builder.Services.AddScoped<AiRequestRecordPersistenceHandler>();

// ── Phase 2 US3 (T068): Runtime-configurable routing thresholds ──
builder.Services.AddRoutingConfigurationServices();

// ── Phase 2 US4 (T078): Prompt registry write endpoints + lifecycle events ──
builder.Services.AddPromptRegistryServices();

// ── Phase 2 US5 (T090): Provider binding management endpoints + invalidation events ──
builder.Services.AddProviderBindingServices();

// ── Phase 2 US6 (T101–T103, T106): AI operations query surface + readiness gate ──
builder.Services.AddAiOperationsQueryServices();
builder.Services.AddSingleton<AiRequestRecordEventConsumer>(sp =>
{
    var consumer = new AiRequestRecordEventConsumer();
    consumer.OnReceived = async (envelope, ct) =>
    {
        using var scope = sp.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AiRequestRecordPersistenceHandler>();
        await handler.HandleAsync(envelope, ct);
    };
    return consumer;
});

// ── Cross-repo integration: MinIO blob + RabbitMQ ingestion publisher ──
builder.Services.Configure<MinioOptions>(builder.Configuration.GetSection("Minio"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));

builder.Services.AddSingleton<Minio.IMinioClient>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MinioOptions>>().Value;
    var host = opts.Endpoint.Split(':')[0];
    var port = opts.Endpoint.Contains(':') ? int.Parse(opts.Endpoint.Split(':')[1]) : 9000;
    var builderChain = new Minio.MinioClient()
        .WithEndpoint(host, port)
        .WithCredentials(opts.AccessKey, opts.SecretKey);
    if (opts.UseSsl) builderChain = builderChain.WithSSL();
    return builderChain.Build();
});
builder.Services.AddSingleton<ICurriculumBlobStore, MinioCurriculumBlobStore>();
builder.Services.AddSingleton<IIngestionJobPublisher, RabbitMqIngestionJobPublisher>();

// ── Phase 3 (T011, T013–T017): Student Experience facade services ──
builder.Services.AddPhase3PlanGate();
builder.Services.AddPhase3SessionEventOutbox();
builder.Services.AddPhase3SessionEventDispatcher();
builder.Services.AddPhase3StudentSessionRepository();
builder.Services.AddPhase3TutorRuntimeClient(builder.Configuration);
builder.Services.AddPhase3CurriculumRetrievalClient(builder.Configuration);
builder.Services.AddPhase3HomeDashboard();
builder.Services.AddPhase3LessonRetrieval();
builder.Services.AddPhase3LessonViewerState();
builder.Services.AddPhase3TutorChatMessageRepository();
builder.Services.AddPhase3VoiceCaptureRepository();
builder.Services.AddPhase3TutorVoiceEndpoint();
builder.Services.AddPhase3QuizDelivery();
builder.Services.AddPhase3MockTest();
builder.Services.AddPhase3HomeworkHelp();
builder.Services.AddPhase3Whiteboard();

// Phase 4 (US4) — engagement ingestion + mastery/streak/badge pipeline.
builder.Services.AddPhase4CorrelationIdPropagator();
builder.Services.AddPhase4FamilyTimezoneResolver();
builder.Services.AddPhase4ProgressRecordRepository();
builder.Services.AddPhase4MasteryStateRepository();
builder.Services.AddPhase4StreakStateRepository();
builder.Services.AddPhase4BadgeCriterionRepository();
builder.Services.AddPhase4BadgeAwardRepository();
builder.Services.AddPhase4BadgeCriterionCatalogueLoader();
builder.Services.AddPhase4MasteryCalculator();
builder.Services.AddPhase4StreakCalculator();
builder.Services.AddPhase4BadgeEvaluator();
builder.Services.AddPhase4ProgressIngestionDeadLetterStore();
builder.Services.AddPhase4DownstreamEventOutbox();
builder.Services.AddPhase4DownstreamEventEmitter();
builder.Services.AddPhase4DownstreamEventDispatcher();
builder.Services.AddPhase4ProgressIngestionWorker();
builder.Services.AddPhase4Phase3EventConsumer();
builder.Services.AddPhase4StudentProgressService();

// Phase 4 (US2) — Parent dashboard + child selector + operator impersonation audit.
builder.Services.AddPhase4DashboardQueryCache();
builder.Services.AddPhase4ChildLinkResolver();
builder.Services.AddPhase4ChildLinkRepository();
builder.Services.AddPhase4ParentProfileRepository();
builder.Services.AddPhase4ParentDashboardService();
builder.Services.AddPhase4OperatorImpersonationAuditor();

// Phase 4 (US3) — Weekly report generation, viewing, sharing, regeneration.
builder.Services.AddPhase4TutorRuntimeClient();
builder.Services.AddPhase4CurriculumRetrievalClient();
builder.Services.AddPhase4GuardrailDecisionTrailStore();
builder.Services.AddPhase4WeeklyReportRepository();
builder.Services.AddPhase4WeeklyReportAggregator();
builder.Services.AddPhase4WeeklyReportSummaryGenerator();
builder.Services.AddPhase4WeeklyReportEventEmitter();
builder.Services.AddPhase4WeeklyReportGenerator();
builder.Services.AddPhase4ShareTokenValidator();
// Background job stays disabled by default; local runs drive generation
// explicitly via IWeeklyReportGenerator / the regenerate endpoint.
builder.Services.Configure<WeeklyReportGenerationJobOptions>(_ => { });
builder.Services.AddPhase4WeeklyReportGenerationJob();

// Phase 4 (US7) — Parent notifications + preferences + local channel stubs.
builder.Services.AddPhase4ParentNotificationRepository();
builder.Services.AddPhase4LocalNotificationChannelStubs();

// ── Phase 9: Identity & Authentication ───────────────────────────
// Registers every identity service, the generalized notification sender,
// the seeders, CORS policy, and (conditionally) Redis-backed adapters
// when REDIS_CONNECTION_STRING is set.
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddPhase4ParentNotificationDispatcher();
builder.Services.AddPhase4NotificationSchedulerHook();

// Phase 4 (US5) — Focus areas grounded in Phase 1 curriculum.
builder.Services.AddPhase4FocusAreaRepository();
builder.Services.AddPhase4FocusAreaSignalCollector();
builder.Services.AddPhase4FocusAreaDeepLinkValidator();
builder.Services.AddPhase4FocusAreaRationaleGenerator();
builder.Services.AddPhase4FocusAreaCalculator();
// Refresh job stays disabled by default; local runs drive RecomputeAsync
// explicitly via IFocusAreaCalculator.
builder.Services.Configure<FocusAreaRefreshJobOptions>(_ => { });
builder.Services.AddPhase4FocusAreaRefreshJob();

// Phase 4 (US8) — At-risk detection + intervention prompts.
builder.Services.AddPhase4AtRiskFlagRepository();
builder.Services.AddPhase4InterventionPromptRepository();
builder.Services.AddPhase4AtRiskThresholdCatalogue();
builder.Services.AddPhase4AtRiskEvaluator();
builder.Services.AddPhase4InterventionPromptGenerator();
builder.Services.AddPhase4AtRiskEventEmitter();
builder.Services.AddPhase4AtRiskDetectionOrchestrator();
// Detection job stays disabled by default; integration tests + the local
// smoke script drive EvaluateStudentAsync directly.
builder.Services.Configure<AtRiskDetectionJobOptions>(_ => { });
builder.Services.AddPhase4AtRiskDetectionJob();

// Phase 5 (US1) — School tenant provisioning + admin onboarding.
builder.Services.AddPhase5DownstreamEventOutbox();
builder.Services.AddPhase5SchoolTenantRepository();
builder.Services.AddPhase5SchoolAdminRepository();
builder.Services.AddPhase5SchoolTenantProvisioningService();
builder.Services.AddPhase5AdminOnboardingService();

// Phase 5 (US2) — Roster import + student onboarding.
builder.Services.AddPhase5RosterImportRepository();
builder.Services.AddPhase5RosterFileStore();
builder.Services.AddPhase5RosterFileParser();
builder.Services.AddPhase5RosterRowValidator();
builder.Services.AddPhase5StudentProfileLinker();
builder.Services.AddPhase5RosterImportWorker();

// Phase 5 (US3) — Class / enrolment / teacher assignment management.
builder.Services.AddPhase5ClassGroupRepository();
builder.Services.AddPhase5ClassEnrolmentRepository();
builder.Services.AddPhase5TeacherRepository();
builder.Services.AddPhase5ClassManagementService();
builder.Services.AddPhase5TeacherAssignmentService();

// Phase 5 (US4) — School admin dashboard + Phase 4 event consumption.
builder.Services.AddPhase5SchoolDashboardQueryCache();
builder.Services.AddPhase5SchoolOperatorImpersonationAuditor();
builder.Services.AddPhase5SchoolAggregateViewRepository();
builder.Services.AddPhase5SchoolDashboardService();
builder.Services.AddPhase5SchoolAggregateViewUpdater();
builder.Services.AddPhase5Phase4EventConsumer();

// Phase 5 (US5) — Teacher dashboard + at-risk notification hook.
builder.Services.AddPhase5TeacherDashboardService();
builder.Services.AddPhase5TeacherAtRiskNotificationHook();

// Phase 5 (US6) — Exams (creation + administration + results).
builder.Services.AddPhase5ExamRepository();
builder.Services.AddPhase5ExamQuestionRepository();
builder.Services.AddPhase5ExamSubmissionRepository();
builder.Services.AddPhase5CustomQuestionGuardrailValidator();
builder.Services.AddPhase5ExamCreationService();
builder.Services.AddPhase5ExamEventEmitter();
builder.Services.AddPhase5ExamAutoGrader();

// Phase 5 (US7) — Leaderboards with privacy controls.
builder.Services.AddPhase5LeaderboardSnapshotRepository();
builder.Services.AddPhase5LeaderboardConfigRepository();
builder.Services.AddPhase5LeaderboardComputationService();

// Phase 5 (US8) — Announcements + school communication.
builder.Services.AddPhase5AnnouncementRepository();
builder.Services.AddPhase5AnnouncementTargetResolver();
builder.Services.AddPhase5AnnouncementDispatcher();
// Scheduler is disabled by default; smoke script + integration tests drive
// RunOnceAsync directly. Production re-enables via configuration.
builder.Services.Configure<AnnouncementSchedulerOptions>(_ => { });
builder.Services.AddPhase5AnnouncementScheduler();

// Phase 5 (US9) — School reports + exportable analytics.
builder.Services.AddPhase5SchoolReportRepository();
builder.Services.AddPhase5SchoolReportAggregator();
builder.Services.AddPhase5SchoolReportExporter();
// Generation job disabled by default; tests + smoke script drive RunOnceAsync.
builder.Services.Configure<SchoolReportGenerationJobOptions>(_ => { });
builder.Services.AddPhase5SchoolReportGenerationJob();

// Phase 5 (US10) — Licensing, seat management, entitlement enforcement.
builder.Services.AddPhase5SchoolLicenseRepository();
builder.Services.AddPhase5SeatWarningNotifier();
builder.Services.AddPhase5LicenseManagementService();
builder.Services.AddPhase5FeatureGateEvaluator();

// ── Phase 6: SaaS Operations, Billing, Security, and Launch Readiness ──
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<Muallimi.Api.Compliance.AuditTrail.AuditTrailWriter>();
builder.Services.AddScoped<Muallimi.Api.DownstreamEvents.Phase6OperationalEventOutbox>();
// Phase 6 US5: compliance (data rights + deletion + export + processing register)
builder.Services.AddScoped<Muallimi.Api.Compliance.DataExport.IDataExportService, Muallimi.Api.Compliance.DataExport.DataExportService>();
Muallimi.Api.Compliance.DataDeletion.DataDeletionServiceExtensions.AddPhase6DataDeletionService(builder.Services);
// Phase 6 US8: Audit trail query/export + data retention service + seed worker
builder.Services.AddScoped<Muallimi.Api.Compliance.AuditTrail.AuditTrailQueryService>();
builder.Services.AddScoped<Muallimi.Api.Compliance.AuditTrail.AuditTrailExportService>();
builder.Services.AddSingleton<Muallimi.Api.Compliance.AuditTrail.AuditTrailExportStore>();
builder.Services.AddScoped<Muallimi.Api.Compliance.DataRetention.DataRetentionService>();
builder.Services.AddHostedService<Muallimi.Api.Compliance.DataRetention.DataRetentionHostedService>();
builder.Services.AddScoped<Muallimi.Api.OperatorManagement.FeatureFlags.FeatureFlagService>();
builder.Services.AddScoped<Muallimi.Api.OperatorManagement.TenantHealth.TenantHealthRollupService>();
builder.Services.AddScoped<Muallimi.Api.OperatorManagement.Impersonation.ImpersonationService>();
builder.Services.AddScoped<Muallimi.Api.OperatorManagement.LaunchReadinessGate.LaunchReadinessGateEvaluator>();
builder.Services.AddSingleton<Muallimi.Api.Security.DataEncryption.IDataEncryptionAdapter>(sp =>
    Muallimi.Api.Security.DataEncryption.LocalAesGcmEncryptionAdapter.FromConfiguration(
        sp.GetRequiredService<IConfiguration>()));
// Resolves the public backend URL for webhook callbacks (ngrok in dev, App:BackendBaseUrl in prod).
builder.Services.AddHttpClient<Muallimi.Api.Payments.IPublicUrlResolver,
    Muallimi.Api.Payments.NgrokPublicUrlResolver>()
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(2));

// Payment providers — add new providers here; no other code changes needed.
builder.Services.AddHttpClient<Muallimi.Api.Payments.PaymentProviderAdapter.IPaymentProviderAdapter,
    Muallimi.Api.Payments.Paymob.PaymobAdapter>();
// Direct registration so the dev-fallback verify endpoint can inject PaymobAdapter.
builder.Services.AddHttpClient<Muallimi.Api.Payments.Paymob.PaymobAdapter>();
builder.Services.AddSingleton<Muallimi.Api.Payments.PaymentProviderAdapter.IPaymentProviderAdapterRegistry>(sp =>
    new Muallimi.Api.Payments.PaymentProviderAdapter.PaymentProviderAdapterRegistry(
        sp.GetServices<Muallimi.Api.Payments.PaymentProviderAdapter.IPaymentProviderAdapter>()));
builder.Services.AddHostedService<Muallimi.Api.DownstreamEvents.Phase6OperationalEventDispatcher>();
builder.Services.AddHostedService<Muallimi.Api.Phase5EventConsumer.Phase5EventConsumer>();
builder.Services.AddHostedService<Muallimi.Api.OperatorManagement.TenantHealth.TenantHealthViewUpdater>();

// ── Phase 6 US1: Billing + Payments MVP ──
builder.Services.AddScoped<Muallimi.Api.Billing.SubscriptionPlans.ISubscriptionPlanService,
    Muallimi.Api.Billing.SubscriptionPlans.SubscriptionPlanService>();
builder.Services.AddScoped<Muallimi.Infrastructure.Billing.Seed.SubscriptionPlanSeeder>();
builder.Services.AddScoped<Muallimi.Api.Billing.SubscriptionLifecycle.ISubscriptionLifecycleService,
    Muallimi.Api.Billing.SubscriptionLifecycle.SubscriptionLifecycleService>();
builder.Services.AddScoped<Muallimi.Api.Billing.SubscriptionLifecycle.IPhase5LicenseSyncService,
    Muallimi.Api.Billing.SubscriptionLifecycle.Phase5LicenseSyncService>();
builder.Services.AddScoped<Muallimi.Api.Billing.InvoiceGeneration.IInvoiceGenerationService,
    Muallimi.Api.Billing.InvoiceGeneration.InvoiceGenerationService>();
builder.Services.AddScoped<Muallimi.Api.Payments.IPaymentTransactionService,
    Muallimi.Api.Payments.PaymentTransactionService>();
builder.Services.AddScoped<Muallimi.Api.Payments.RefundProcessing.IRefundService,
    Muallimi.Api.Payments.RefundProcessing.RefundService>();
// ── Phase 6 US7: Payment provider integration extensions (T109–T113) ──
builder.Services.AddScoped<Muallimi.Api.Payments.PaymentProviderAdapter.IPaymentMethodManagementService,
    Muallimi.Api.Payments.PaymentProviderAdapter.PaymentMethodManagementService>();
// Webhook signature validators — one per provider, registered in the same order as adapters.
builder.Services.AddSingleton<Muallimi.Api.Payments.WebhookProcessing.IWebhookSignatureValidator,
    Muallimi.Api.Payments.Paymob.PaymobWebhookSignatureValidator>();
builder.Services.AddSingleton<Muallimi.Api.Payments.WebhookProcessing.WebhookSignatureValidatorRegistry>();
builder.Services.AddScoped<Muallimi.Api.Payments.Idempotency.PaymentIdempotencyService>();
builder.Services.Configure<Muallimi.Api.Payments.RetryPolicy.PaymentRetryOptions>(_ => { });
builder.Services.AddSingleton<Muallimi.Api.Payments.RetryPolicy.PaymentRetryScheduler>();
builder.Services.AddHostedService<Muallimi.Api.Payments.RetryPolicy.PaymentRetryHostedService>();
builder.Services.Configure<Muallimi.Api.Billing.BillingCycleEngine.BillingCycleEngineOptions>(_ => { });
builder.Services.AddSingleton<Muallimi.Api.Billing.BillingCycleEngine.BillingCycleEngine>();
builder.Services.AddSingleton<Muallimi.Api.Billing.BillingCycleEngine.IBillingCycleEngine>(sp =>
    sp.GetRequiredService<Muallimi.Api.Billing.BillingCycleEngine.BillingCycleEngine>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<Muallimi.Api.Billing.BillingCycleEngine.BillingCycleEngine>());

// ── Phase 6 US2: Multi-channel notification delivery ──
builder.Services.AddPhase6ProductionProviderBindings();
builder.Services.AddPhase6NotificationDeliveryTracker();
builder.Services.Configure<Muallimi.Api.Notifications.RetryAndDeadLetter.NotificationRetryHostedServiceOptions>(_ => { });
builder.Services.AddPhase6NotificationRetryService();
builder.Services.AddPhase6BillingNotificationDispatcher();

// ── Phase 6 US3: AI Operations Dashboard ──
builder.Services.AddPhase6AIMetricConsumer();
builder.Services.Configure<Muallimi.Api.AiOperations.AlertRuleEngine.AlertRuleEvaluatorOptions>(_ => { });
builder.Services.AddPhase6AlertRuleEvaluator();

// ── Phase 6 US4: Observability, Logging, and Incident Response ──
builder.Services.AddScoped<Muallimi.Api.Observability.DistributedTracing.DistributedTraceQueryService>();
builder.Services.AddScoped<Muallimi.Api.AiOperations.IncidentManagement.IIncidentManagementService,
    Muallimi.Api.AiOperations.IncidentManagement.IncidentManagementService>();
builder.Services.AddHttpClient("health-alert");
builder.Services.Configure<Muallimi.Api.Observability.HealthChecks.HealthCheckAlertOptions>(
    builder.Configuration.GetSection("HealthCheckAlerts"));
builder.Services.AddSingleton<Muallimi.Api.Observability.HealthChecks.IHealthAlertSink,
    Muallimi.Api.Observability.HealthChecks.LoggingHealthAlertSink>();
builder.Services.AddHostedService<Muallimi.Api.Observability.HealthChecks.HealthCheckAlertService>();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS (dev: allow the frontend on :3000)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

var app = builder.Build();

// ── Phase 9 US3 T107 / US7 T166: migrate / seed / backfill CLI commands ──
// `dotnet run -- migrate`  applies every EF migration and exits.
// `dotnet run -- seed`     runs the Identity + retention seeders and exits.
// `dotnet run -- backfill --source-schema <schema> [--dry-run] [--verify]`
//                          runs or verifies the legacy AuthAPI backfill.
// Any other arg list boots the web host as usual.
if (args.Length > 0 && (string.Equals(args[0], "migrate", StringComparison.Ordinal)
    || string.Equals(args[0], "seed", StringComparison.Ordinal)
    || string.Equals(args[0], "backfill", StringComparison.Ordinal)))
{
    using var cliScope = app.Services.CreateScope();
    var sp = cliScope.ServiceProvider;
    if (args[0] == "migrate")
    {
        var db = sp.GetRequiredService<Muallimi.Infrastructure.Persistence.MuallimiDbContext>();
        app.Logger.LogInformation("CLI migrate: applying EF migrations...");
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("CLI migrate: complete.");
    }
    else
    {
        app.Logger.LogInformation("CLI seed: running identity + retention seeders...");
        try
        {
            var retentionDb = sp.GetRequiredService<Muallimi.Infrastructure.Persistence.MuallimiDbContext>();
            await Muallimi.Api.Compliance.DataRetention.DefaultRetentionPolicySeeder.EnsureSeededAsync(retentionDb);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "CLI seed: retention policy seed skipped.");
        }
        var runner = sp.GetRequiredService<IdentitySeedRunner>();
        await runner.RunAsync();
        app.Logger.LogInformation("CLI seed: complete.");
    }
    return;
}

// ── Phase 9 US7 T166: backfill CLI ────────────────────────────────────────
if (args.Length > 0 && string.Equals(args[0], "backfill", StringComparison.Ordinal))
{
    // Parse args: --source-schema <name>  [--dry-run | --verify]
    var sourceSchema = "legacy_auth";
    var dryRun = false;
    var verify = false;

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--source-schema" && i + 1 < args.Length)
            sourceSchema = args[++i];
        else if (args[i] == "--dry-run")
            dryRun = true;
        else if (args[i] == "--verify")
            verify = true;
    }

    using var backfillScope = app.Services.CreateScope();
    var bsp = backfillScope.ServiceProvider;
    var backfillRunner = bsp.GetRequiredService<Muallimi.Infrastructure.Identity.Adapters.BackfillScriptRunner>();

    if (verify)
    {
        app.Logger.LogInformation("CLI backfill: verifying source-schema={Schema}...", sourceSchema);
        var vr = await backfillRunner.VerifyAsync(sourceSchema);
        if (!vr.Passed)
        {
            foreach (var f in vr.Failures)
                app.Logger.LogError("Verify failure: {Failure}", f);
            Environment.Exit(1);
        }
        app.Logger.LogInformation("CLI backfill verify: all invariants passed.");
    }
    else
    {
        app.Logger.LogInformation(
            "CLI backfill [{Mode}]: source-schema={Schema}",
            dryRun ? "dry-run" : "apply", sourceSchema);
        var br = await backfillRunner.RunAsync(sourceSchema, dryRun);
        if (br.Errors.Count > 0)
        {
            foreach (var e in br.Errors)
                app.Logger.LogError("Backfill error: {Error}", e);
            Environment.Exit(1);
        }
        app.Logger.LogInformation(
            "CLI backfill complete: created={C} skipped={S} roles={R}",
            br.UsersCreated, br.UsersSkipped, br.RolesGranted);
    }
    return;
}

// Middleware pipeline
app.UseCors();
// Phase 6 US5: Transport security (TLS/HSTS/baseline headers) + child-safety controls
Muallimi.Api.Security.TransportSecurity.TransportSecurityExtensions.UsePhase6TransportSecurity(app);
Muallimi.Api.Security.ChildSafetyControls.ChildSafetyControlsExtensions.UsePhase6ChildSafetyControls(app);
// Phase 6 US5: Wire column-encryption adapter for EF value converters
Muallimi.Api.Security.DataEncryption.ColumnEncryptionWiring.UsePhase6ColumnEncryption(app);

// Phase 9: security headers + tenant-resolution middleware. Both run
// BEFORE correlation-id + entitlement middleware so downstream handlers
// see the resolved tenant and consistent response headers.
app.UseIdentitySecurityHeaders();
// Phase 9: ASP.NET authentication/authorization. Must run BEFORE
// UseIdentityTenantResolution so context.User.Identity?.IsAuthenticated
// is true when the JWT is present — otherwise the tenant claim is
// ignored and only the legacy X-Tenant-Id header is honoured.
app.UseAuthentication();
app.UseAuthorization();
app.UseIdentityTenantResolution();
// Add-child redesign Phase 5.4: keep parent session alive while
// any derived child session is actively making requests.
app.UseDerivedSessionKeepalive();
// Add-child redesign Phase 7: child-scope JWTs are blocked from
// parent-only path prefixes (billing, parent dashboard, etc.).
app.UseScopeEnforcement();
app.UseImpersonationContext();

app.UseCorrelationId();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Phase 6: Entitlement enforcement on authenticated requests
// MUST be registered BEFORE any MapXxx call. In ASP.NET Core, calling
// UseMiddleware AFTER routes have been mapped causes the EndpointRouteBuilder
// to be re-created, dropping all previously-registered routes from the
// effective pipeline. (Symptom: routes compile in the DLL and the MapGet
// runs at startup, but requests return route-level 404 with no body.)
app.UsePhase6EntitlementEnforcement();
// Phase 4 US2 — operator impersonation middleware. Same constraint as above:
// must run BEFORE any MapXxx, otherwise routes registered after this call
// land in a freshly-created EndpointRouteBuilder that the request pipeline
// never sees, so they 404 at runtime even though the DLL contains them.
app.UseOperatorImpersonation();

// Health check (legacy + Phase 6 readiness/liveness/startup probes)
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "muallimi-main-backend" }));
app.MapPhase6HealthChecks();

// Phase 9: identity endpoint group (module probe today; full auth
// endpoints land in US1-US7).
app.MapIdentityEndpoints();

// Phase 6 US1: Billing + Payments endpoints
Muallimi.Api.Billing.BillingEndpoints.MapBillingEndpoints(app);
Muallimi.Api.Payments.PaymentInitiateEndpoints.MapPaymentInitiateEndpoints(app);
Muallimi.Api.Payments.WebhookProcessing.PaymentWebhookEndpoints.MapPaymentWebhooks(app);
// Phase 6 US5: Compliance (data rights + processing register) endpoints
Muallimi.Api.Compliance.ComplianceEndpoints.MapComplianceEndpoints(app);
// Phase 6 US8: Audit trail + data retention operator endpoints
Muallimi.Api.Compliance.AuditTrail.AuditTrailEndpoints.MapAuditTrailEndpoints(app);
Muallimi.Api.Compliance.DataRetention.DataRetentionEndpoints.MapDataRetentionEndpoints(app);

// ── Phase 3 US1: Student Experience endpoints (session lifecycle + plan gate) ──
app.MapStudentExperience();

// ── Phase 4 US1: Student Progress Surface (mastery / streak / badges / focus areas) ──
app.MapStudentProgressSurface();

// ── Phase 4 US2: Parent dashboard endpoints (impersonation middleware moved up to pre-route block) ──
app.MapParents();

// ── Phase 4 US3: Weekly report view / share / regenerate + shared-report public route ──
app.MapEngagement();

// ── Phase 5 US1: School tenant provisioning + admin onboarding + school config ──
app.MapSchoolManagement();

// ── Phase 5 US6: Exam creation, administration, results ──
app.MapExams();

// ── Phase 5 US7: Leaderboards config + role-scoped queries ──
app.MapLeaderboards();

// ── Phase 5 US8: Announcements + school communication ──
app.MapAnnouncements();

// ── Phase 5 US9: School reports + exportable analytics ──
app.MapSchoolReports();

// ── Curriculum Admin API: Upload & Ingestion ──

app.MapCurriculumSourceList();
app.MapCurriculumNodeChunks();
app.MapCurriculumSourcePipeline();
app.MapCurriculumSourceDelete();

// Upload a new curriculum source. MVP behaviour: this endpoint ONLY stores
// the file + metadata. It deliberately does not enqueue an ingestion job —
// the admin must explicitly trigger extraction from /admin/curriculum/sources/{id}/extract.
// This separation lets us measure each stage in isolation (upload accuracy,
// extraction quality, review throughput) before stitching them into an
// automatic pipeline in a later phase.
app.MapPost("/admin/curriculum/upload", async (
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit,
    ICurriculumBlobStore blobStore) =>
{
    if (!httpContext.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Multipart form data required." });

    var form = await httpContext.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "A curriculum file is required." });

    var curriculumType = form["curriculum_type"].ToString();
    var grade = form["grade"].ToString();
    var subject = form["subject"].ToString();
    var academicYear = form["academic_year"].ToString();
    var tutorLanguage = form["tutor_language"].ToString();

    if (string.IsNullOrWhiteSpace(curriculumType) || string.IsNullOrWhiteSpace(grade)
        || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(academicYear)
        || string.IsNullOrWhiteSpace(tutorLanguage))
    {
        return Results.BadRequest(new { error = "curriculum_type, grade, subject, academic_year, and tutor_language are required." });
    }

    // Parse enums
    if (!Enum.TryParse<Muallimi.Domain.Shared.CurriculumType>(curriculumType, ignoreCase: true, out var ctEnum))
        return Results.BadRequest(new { error = $"Invalid curriculum_type '{curriculumType}'. Allowed: Moe, LanguageSchool, International." });
    if (!Enum.TryParse<Muallimi.Domain.Shared.Grade>(grade, ignoreCase: true, out var gradeEnum))
        return Results.BadRequest(new { error = $"Invalid grade '{grade}'. Allowed: Grade7." });
    if (!Enum.TryParse<Muallimi.Domain.Shared.Subject>(subject, ignoreCase: true, out var subjectEnum))
        return Results.BadRequest(new { error = $"Invalid subject '{subject}'. Allowed: Mathematics, Science, ArabicLanguage, EnglishLanguage." });
    if (!Enum.TryParse<Muallimi.Domain.Shared.TutorLanguage>(tutorLanguage, ignoreCase: true, out var langEnum))
        return Results.BadRequest(new { error = $"Invalid tutor_language '{tutorLanguage}'. Allowed: Ar, En." });

    // Determine file format from extension
    var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant();
    Muallimi.Domain.Shared.FileFormat format = ext switch
    {
        ".pdf" => Muallimi.Domain.Shared.FileFormat.Pdf,
        ".docx" => Muallimi.Domain.Shared.FileFormat.Docx,
        ".html" or ".htm" => Muallimi.Domain.Shared.FileFormat.Html,
        _ => throw new InvalidOperationException($"Unsupported file format '{ext}'. Accepted: .pdf, .docx, .html")
    };
    Muallimi.Domain.Curriculum.CurriculumSource.ValidateFormat(format);

    // Compute content hash
    using var stream = file.OpenReadStream();
    using var sha = System.Security.Cryptography.SHA256.Create();
    var hashBytes = await sha.ComputeHashAsync(stream);
    var contentHash = Convert.ToHexStringLower(hashBytes);
    stream.Position = 0;

    // Check for duplicate (same scope + same hash)
    var existing = await db.CurriculumSources
        .Where(s => s.CurriculumType == ctEnum && s.Grade == gradeEnum && s.Subject == subjectEnum
                    && s.TutorLanguage == langEnum && s.AcademicYear == academicYear
                    && s.ContentHash == contentHash
                    && s.Status != Muallimi.Domain.Shared.SourceStatus.Replaced)
        .FirstOrDefaultAsync();

    if (existing is not null)
        return Results.Conflict(new { error = "Identical file already uploaded for this scope.", source_id = existing.SourceId });

    // Upload file to MinIO — bucket is auto-created on first run.
    var storageKey = $"curriculum/{ctEnum}/{gradeEnum}/{subjectEnum}/{Guid.NewGuid()}{ext}";
    await blobStore.UploadAsync(storageKey, stream, file.ContentType ?? "application/octet-stream", httpContext.RequestAborted);

    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var source = Muallimi.Domain.Curriculum.CurriculumSource.Create(
        ctEnum, gradeEnum, subjectEnum, academicYear, langEnum, format, storageKey, contentHash, actor,
        originalFileName: file.FileName);

    db.CurriculumSources.Add(source);
    await db.SaveChangesAsync();

    // No IngestionJob and no RabbitMQ publish here. The source is parked in
    // SourceStatus.Received (the frontend renders this as
    // "Ready for extracting") until the admin explicitly calls
    // POST /admin/curriculum/sources/{sourceId}/extract.

    audit.Emit(new AuditEvent
    {
        EventCategory = "curriculum",
        Action = "upload",
        TargetType = "CurriculumSource",
        TargetId = source.SourceId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Created($"/admin/curriculum/sources/{source.SourceId}", new
    {
        source_id = source.SourceId,
        status = source.Status.ToString(),
        correlation_id = correlationId
    });
})
.WithName("UploadCurriculum")
.WithTags("Curriculum")
.DisableAntiforgery();

// Explicit per-source extraction trigger for the MVP stage-separated flow.
// The admin uploads via POST /admin/curriculum/upload (no auto-pipeline);
// then, when ready, hits this endpoint to enqueue a Claude-driven structure
// extraction job. The worker reads ExtractOnly=true on the message and
// stops after writing the structure tree + lessons (no chunking, no
// embedding) so we can measure extraction accuracy independently before
// the rest of the pipeline runs.
app.MapPost("/admin/curriculum/sources/{sourceId:guid}/extract", async (
    Guid sourceId,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit,
    IIngestionJobPublisher publisher) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var source = await db.CurriculumSources.FindAsync(new object[] { sourceId }, httpContext.RequestAborted);
    if (source is null)
        return Results.NotFound(new { error = "Source not found." });

    // Only sources still parked in Received (uploaded but not yet extracted)
    // are eligible. Anything further along the pipeline must be deleted +
    // re-uploaded to re-extract.
    if (source.Status != Muallimi.Domain.Shared.SourceStatus.Received)
    {
        return Results.Conflict(new
        {
            error = $"Cannot start extraction for a source in status '{source.Status}'. " +
                    "Only sources in 'Received' (Ready for extracting) can be extracted.",
            current_status = source.Status.ToString()
        });
    }

    var job = Muallimi.Domain.Content.IngestionJob.Create(source.SourceId, correlationId);
    db.IngestionJobs.Add(job);

    // Transition the source: Received → Ingesting. The worker will move it
    // on to Extracted via the /internal/ingestion/results callback when the
    // structure extraction completes.
    source.MarkIngesting();

    await db.SaveChangesAsync(httpContext.RequestAborted);

    await publisher.PublishAsync(new IngestionMessage(
        JobId: job.JobId,
        SourceId: source.SourceId,
        StorageKey: source.StorageKey,
        CurriculumType: source.CurriculumType.ToString(),
        Grade: source.Grade.ToString(),
        Subject: source.Subject.ToString(),
        TutorLanguage: source.TutorLanguage.ToString(),
        AcademicYear: source.AcademicYear,
        FileFormat: source.FileFormat.ToString(),
        ContentHash: source.ContentHash,
        CorrelationId: correlationId,
        ExtractOnly: true),
        httpContext.RequestAborted);

    audit.Emit(new AuditEvent
    {
        EventCategory = "curriculum",
        Action = "extract-triggered",
        TargetType = "CurriculumSource",
        TargetId = source.SourceId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Accepted($"/admin/curriculum/sources/{source.SourceId}", new
    {
        source_id = source.SourceId,
        job_id = job.JobId,
        status = source.Status.ToString(),
        correlation_id = correlationId
    });
})
.WithName("StartCurriculumExtraction")
.WithTags("Curriculum");

// ── MVP stage-separated flow: review transitions ────────────────────────
//
// Send-for-review: Extracted → InReview. Called when the admin finishes
// auditing the extracted structure on the workbench (surface 3) and is
// ready to hand it off to the structured review pass (surface 4).
app.MapPost("/admin/curriculum/sources/{sourceId:guid}/send-for-review", async (
    Guid sourceId,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var source = await db.CurriculumSources.FindAsync(new object[] { sourceId }, httpContext.RequestAborted);
    if (source is null)
        return Results.NotFound(new { error = "Source not found." });

    try { source.MarkInReview(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message, current_status = source.Status.ToString() }); }

    await db.SaveChangesAsync(httpContext.RequestAborted);

    audit.Emit(new AuditEvent
    {
        EventCategory = "curriculum",
        Action = "send-for-review",
        TargetType = "CurriculumSource",
        TargetId = source.SourceId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new { source_id = source.SourceId, status = source.Status.ToString(), correlation_id = correlationId });
})
.WithName("SendCurriculumForReview")
.WithTags("Curriculum");

// Approve: InReview / Extracted → Approved. Signing off on the structure
// for downstream chunk + embed (future trigger).
app.MapPost("/admin/curriculum/sources/{sourceId:guid}/approve", async (
    Guid sourceId,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var source = await db.CurriculumSources.FindAsync(new object[] { sourceId }, httpContext.RequestAborted);
    if (source is null)
        return Results.NotFound(new { error = "Source not found." });

    try { source.MarkApproved(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message, current_status = source.Status.ToString() }); }

    await db.SaveChangesAsync(httpContext.RequestAborted);

    audit.Emit(new AuditEvent
    {
        EventCategory = "curriculum",
        Action = "approve-structure",
        TargetType = "CurriculumSource",
        TargetId = source.SourceId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new { source_id = source.SourceId, status = source.Status.ToString(), correlation_id = correlationId });
})
.WithName("ApproveCurriculumStructure")
.WithTags("Curriculum");

// Request re-extract: any non-terminal post-upload state → Received. The
// admin can then trigger /extract again. We also delete the existing
// CurriculumStructure rows so the next extraction can write fresh ones.
app.MapPost("/admin/curriculum/sources/{sourceId:guid}/request-reextract", async (
    Guid sourceId,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var source = await db.CurriculumSources.FindAsync(new object[] { sourceId }, httpContext.RequestAborted);
    if (source is null)
        return Results.NotFound(new { error = "Source not found." });

    try { source.ResetForReextract(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message, current_status = source.Status.ToString() }); }

    // Clear any existing structure + lessons so the re-extraction starts clean.
    var structureIds = await db.CurriculumStructures
        .Where(s => s.SourceId == sourceId)
        .Select(s => s.StructureId)
        .ToListAsync(httpContext.RequestAborted);
    if (structureIds.Count > 0)
    {
        var lessonIds = await db.Lessons
            .Where(l => structureIds.Contains(l.StructureId))
            .Select(l => l.LessonId)
            .ToListAsync(httpContext.RequestAborted);
        if (lessonIds.Count > 0)
            await db.Lessons.Where(l => lessonIds.Contains(l.LessonId)).ExecuteDeleteAsync(httpContext.RequestAborted);
        await db.CurriculumStructures.Where(s => structureIds.Contains(s.StructureId)).ExecuteDeleteAsync(httpContext.RequestAborted);
    }

    await db.IngestionJobs.Where(j => j.SourceId == sourceId).ExecuteDeleteAsync(httpContext.RequestAborted);
    await db.SaveChangesAsync(httpContext.RequestAborted);

    audit.Emit(new AuditEvent
    {
        EventCategory = "curriculum",
        Action = "request-reextract",
        TargetType = "CurriculumSource",
        TargetId = source.SourceId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new { source_id = source.SourceId, status = source.Status.ToString(), correlation_id = correlationId });
})
.WithName("RequestCurriculumReextract")
.WithTags("Curriculum");

// Replace the extracted structure JSON for a source. The Review surface
// posts the edited tree here (the whole tree, not a delta) so the admin
// can rename, delete, and reorder nodes without taking on the complexity
// of incremental patches in the MVP. Allowed in Extracted / InReview.
app.MapMethods("/admin/curriculum/sources/{sourceId:guid}/structure", new[] { "PATCH" }, async (
    Guid sourceId,
    StructureUpdatePayload payload,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    if (string.IsNullOrWhiteSpace(payload.Nodes))
        return Results.BadRequest(new { error = "nodes JSON is required." });

    var source = await db.CurriculumSources.FindAsync(new object[] { sourceId }, httpContext.RequestAborted);
    if (source is null)
        return Results.NotFound(new { error = "Source not found." });

    if (source.Status != Muallimi.Domain.Shared.SourceStatus.Extracted
        && source.Status != Muallimi.Domain.Shared.SourceStatus.InReview)
    {
        return Results.Conflict(new
        {
            error = $"Structure can only be edited in Extracted or InReview. Current: {source.Status}.",
            current_status = source.Status.ToString()
        });
    }

    var structure = await db.CurriculumStructures
        .Where(s => s.SourceId == sourceId)
        .OrderByDescending(s => s.ExtractedAt)
        .FirstOrDefaultAsync(httpContext.RequestAborted);
    if (structure is null)
        return Results.NotFound(new { error = "No structure to edit." });

    structure.UpdateNodes(payload.Nodes);
    await db.SaveChangesAsync(httpContext.RequestAborted);

    audit.Emit(new AuditEvent
    {
        EventCategory = "curriculum",
        Action = "edit-structure",
        TargetType = "CurriculumStructure",
        TargetId = structure.StructureId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new { source_id = sourceId, structure_id = structure.StructureId, correlation_id = correlationId });
})
.WithName("UpdateCurriculumStructure")
.WithTags("Curriculum");

// Inline PDF preview: streams the stored file from MinIO so the workbench
// can render it side-by-side with the extracted tree. Inline disposition
// so the browser opens it in-iframe instead of forcing a download.
app.MapGet("/admin/curriculum/sources/{sourceId:guid}/download", async (
    Guid sourceId,
    MuallimiDbContext db,
    ICurriculumBlobStore blobStore,
    HttpContext httpContext) =>
{
    var source = await db.CurriculumSources.FindAsync(new object[] { sourceId }, httpContext.RequestAborted);
    if (source is null)
        return Results.NotFound(new { error = "Source not found." });

    BlobDownloadResult download;
    try
    {
        download = await blobStore.DownloadAsync(source.StorageKey, httpContext.RequestAborted);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to load source file.",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }

    var fileName = !string.IsNullOrWhiteSpace(source.OriginalFileName)
        ? source.OriginalFileName
        : Path.GetFileName(source.StorageKey);

    // Inline disposition so the workbench iframe renders the PDF instead of
    // triggering a download. Filename is still set for the "Open in new tab"
    // path so the browser shows a sensible tab title + save-as default.
    var safeName = System.Uri.EscapeDataString(fileName);
    httpContext.Response.Headers.ContentDisposition =
        $"inline; filename=\"{fileName}\"; filename*=UTF-8''{safeName}";
    httpContext.Response.Headers.CacheControl = "private, max-age=300";

    // The global security middleware sets X-Frame-Options: DENY and a
    // restrictive CSP that prevent the curriculum-admin workbench iframe
    // (frontend on :3000) from rendering this PDF (backend on :5063).
    // Override both for this single read-only download endpoint so the
    // PDF preview pane works in dev. Production tightens this via an env-
    // driven allow-list of frontend origins.
    httpContext.Response.Headers.Remove("X-Frame-Options");
    var allowedFrontend = httpContext.RequestServices
        .GetService<IConfiguration>()?["FrontendOrigin"] ?? "http://localhost:3000";
    httpContext.Response.Headers["Content-Security-Policy"] =
        $"default-src 'self'; frame-ancestors 'self' {allowedFrontend}";

    return Results.Stream(download.Content, contentType: download.ContentType, enableRangeProcessing: true);
})
.WithName("DownloadCurriculumSource")
.WithTags("Curriculum");

// Re-publish queued ingestion jobs that never made it onto the bus
// (e.g. uploads that landed before the publisher was wired, or after a
// transient Rabbit outage). Idempotent — safe to call repeatedly.
app.MapPost("/admin/curriculum/jobs/republish", async (
    MuallimiDbContext db,
    IIngestionJobPublisher publisher,
    HttpContext httpContext) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();

    var orphans = await db.IngestionJobs
        .Where(j => j.Status == Muallimi.Domain.Shared.IngestionJobStatus.Queued)
        .Join(db.CurriculumSources, j => j.SourceId, s => s.SourceId, (j, s) => new { Job = j, Source = s })
        .ToListAsync();

    var republished = new List<Guid>();
    foreach (var row in orphans)
    {
        await publisher.PublishAsync(new IngestionMessage(
            JobId: row.Job.JobId,
            SourceId: row.Source.SourceId,
            StorageKey: row.Source.StorageKey,
            CurriculumType: row.Source.CurriculumType.ToString(),
            Grade: row.Source.Grade.ToString(),
            Subject: row.Source.Subject.ToString(),
            TutorLanguage: row.Source.TutorLanguage.ToString(),
            AcademicYear: row.Source.AcademicYear,
            FileFormat: row.Source.FileFormat.ToString(),
            ContentHash: row.Source.ContentHash,
            CorrelationId: row.Job.CorrelationId ?? correlationId),
            httpContext.RequestAborted);
        republished.Add(row.Job.JobId);
    }

    return Results.Ok(new
    {
        republished_count = republished.Count,
        job_ids = republished,
        correlation_id = correlationId
    });
})
.WithName("RepublishIngestionJobs")
.WithTags("Curriculum");

// T028: GET ingestion job status
app.MapGet("/admin/curriculum/jobs/{jobId:guid}", async (Guid jobId, MuallimiDbContext db) =>
{
    var job = await db.IngestionJobs.FindAsync(jobId);
    if (job is null)
        return Results.NotFound(new { error = "Job not found." });

    // Count concurrent in-flight jobs (any tenant) that started before this
    // one. The frontend uses this to show "another extraction is running,
    // yours will start next" while the worker is busy. Excludes self even
    // if it's processing — the value answers "how many AHEAD of me", not
    // "how many in the system".
    var processingAhead = await db.IngestionJobs
        .CountAsync(j => j.JobId != jobId
            && j.Status == Muallimi.Domain.Shared.IngestionJobStatus.Processing);

    return Results.Ok(new
    {
        job_id = job.JobId,
        source_id = job.SourceId,
        status = job.Status.ToString(),
        stages = job.Stages,
        started_at = job.StartedAt,
        completed_at = job.CompletedAt,
        error_reason = job.ErrorReason,
        correlation_id = job.CorrelationId,
        processing_jobs_ahead = processingAhead
    });
})
.WithName("GetIngestionJobStatus")
.WithTags("Curriculum");

// T029: GET curriculum structure tree
app.MapGet("/admin/curriculum/{sourceId:guid}/structure", async (Guid sourceId, MuallimiDbContext db) =>
{
    var structure = await db.CurriculumStructures
        .Where(s => s.SourceId == sourceId)
        .OrderByDescending(s => s.ExtractedAt)
        .FirstOrDefaultAsync();

    if (structure is null)
        return Results.NotFound(new { error = "Structure not found for this source." });

    return Results.Ok(new
    {
        structure_id = structure.StructureId,
        source_id = structure.SourceId,
        nodes = structure.Nodes,
        extracted_at = structure.ExtractedAt
    });
})
.WithName("GetCurriculumStructure")
.WithTags("Curriculum");

// T029: GET lesson detail with chunks
app.MapGet("/admin/curriculum/{sourceId:guid}/structure/{lessonId:guid}", async (
    Guid sourceId, Guid lessonId, MuallimiDbContext db) =>
{
    var lesson = await db.Lessons.FindAsync(lessonId);
    if (lesson is null)
        return Results.NotFound(new { error = "Lesson not found." });

    var chunks = await db.ContentChunks
        .Where(c => c.LessonId == lessonId)
        .OrderBy(c => c.Sequence)
        .Select(c => new
        {
            chunk_id = c.ChunkId,
            sequence = c.Sequence,
            text = c.Text,
            math_blocks = c.MathBlocks,
            token_count = c.TokenCount,
            source_refs = c.SourceRefs,
            status = c.Status.ToString()
        })
        .ToListAsync();

    return Results.Ok(new
    {
        lesson_id = lesson.LessonId,
        path = lesson.Path,
        curriculum_type = lesson.CurriculumType.ToString(),
        grade = lesson.Grade.ToString(),
        subject = lesson.Subject.ToString(),
        tutor_language = lesson.TutorLanguage.ToString(),
        content_hash = lesson.ContentHash,
        status = lesson.Status.ToString(),
        chunks
    });
})
.WithName("GetLessonDetail")
.WithTags("Curriculum");

// ── T040: Internal Ingestion Result Handler ──
// Called by document-ingestion worker to report job status updates

app.MapPut("/internal/ingestion/jobs/{jobId:guid}/status", async (
    Guid jobId, IngestionJobStatusUpdate update, MuallimiDbContext db) =>
{
    var job = await db.IngestionJobs.FindAsync(jobId);
    if (job is null)
        return Results.NotFound();

    var stagesJson = System.Text.Json.JsonSerializer.Serialize(update.Stages);
    job.UpdateStage(stagesJson);

    if (update.Status == "processing" && job.Status == Muallimi.Domain.Shared.IngestionJobStatus.Queued)
        job.MarkProcessing();
    else if (update.Status == "completed")
        job.MarkCompleted();
    else if (update.Status == "failed")
        job.MarkFailed(update.ErrorReason ?? "Unknown error");

    // Also update the source status. For the MVP extract-only flow the worker
    // sets extract_only=true on the completion call so we stop at Extracted
    // instead of advancing through to Indexed. Legacy (full pipeline) callers
    // get the original behaviour because the default is false.
    var source = await db.CurriculumSources.FindAsync(job.SourceId);
    if (source is not null)
    {
        if (update.Status == "processing" && source.Status == Muallimi.Domain.Shared.SourceStatus.Received)
            source.MarkIngesting();
        else if (update.Status == "completed")
        {
            if (update.ExtractOnly)
                source.MarkExtracted();
            else
                source.MarkIndexed();
        }
        else if (update.Status == "failed")
            source.MarkFailed(update.ErrorReason ?? "Unknown error");
    }

    await db.SaveChangesAsync();
    return Results.Ok();
})
.WithName("UpdateIngestionJobStatus")
.WithTags("Internal");

// Called by document-ingestion worker to report full ingestion results
app.MapPost("/internal/ingestion/results", async (
    IngestionResultPayload payload, MuallimiDbContext db, AuditEventEmitter audit) =>
{
    // Create the curriculum structure
    var structure = Muallimi.Domain.Curriculum.CurriculumStructure.Create(
        payload.SourceId, payload.StructureNodes);
    db.CurriculumStructures.Add(structure);

    // Look up the source to get scope metadata
    var source = await db.CurriculumSources.FindAsync(payload.SourceId);
    if (source is null)
        return Results.NotFound(new { error = "Source not found." });

    // Create lessons (and chunks, when present). In the MVP extract-only path
    // payload.Lessons carries skeletons with no chunks — the chunks loop just
    // skips quietly and lessons land in `Ingested` state ready for the future
    // chunk + embed trigger.
    foreach (var lessonData in payload.Lessons)
    {
        var lesson = Muallimi.Domain.Curriculum.Lesson.Create(
            structure.StructureId,
            source.CurriculumType,
            source.Grade,
            source.Subject,
            source.TutorLanguage,
            lessonData.Path);

        lesson.SetContentHash(lessonData.ContentHash);
        lesson.MarkIngested(payload.ExtractOnly ? "Structure extracted" : "Initial ingestion");
        db.Lessons.Add(lesson);

        foreach (var chunkData in lessonData.Chunks)
        {
            var chunk = Muallimi.Domain.Curriculum.ContentChunk.Create(
                lesson.LessonId,
                chunkData.Sequence,
                chunkData.Text,
                chunkData.TokenCount,
                chunkData.OverlapWithPrevious,
                chunkData.SourceRefs,
                chunkData.Metadata,
                chunkData.MathBlocks);

            if (chunkData.Embedding is not null && chunkData.EmbeddingModelVersion is not null)
            {
                chunk.SetEmbedding(chunkData.Embedding, chunkData.EmbeddingModelVersion);
            }

            chunk.Activate();
            db.ContentChunks.Add(chunk);
        }
    }

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "curriculum",
        Action = "ingestion-completed",
        TargetType = "CurriculumSource",
        TargetId = payload.SourceId.ToString(),
        ActorId = "ingestion-worker",
        TenantId = "system",
        Outcome = "succeeded",
        CorrelationId = payload.CorrelationId
    });

    return Results.Ok(new
    {
        structure_id = structure.StructureId,
        lesson_count = payload.Lessons.Count,
        chunk_count = payload.Lessons.Sum(l => l.Chunks.Count)
    });
})
.WithName("ReceiveIngestionResults")
.WithTags("Internal");

// ── T051: Asset Generation Trigger Endpoints ──

// POST /admin/content/generate/batch — trigger generation across a scope
app.MapPost("/admin/content/generate/batch", async (
    HttpContext httpContext,
    GenerationBatchRequest request,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    // Find all ingested lessons in the requested scope
    var query = db.Lessons
        .Where(l => l.Status == Muallimi.Domain.Shared.LessonStatus.Ingested);

    if (request.CurriculumType is not null &&
        Enum.TryParse<Muallimi.Domain.Shared.CurriculumType>(request.CurriculumType, ignoreCase: true, out var ct))
        query = query.Where(l => l.CurriculumType == ct);

    if (request.Grade is not null &&
        Enum.TryParse<Muallimi.Domain.Shared.Grade>(request.Grade, ignoreCase: true, out var gr))
        query = query.Where(l => l.Grade == gr);

    if (request.Subject is not null &&
        Enum.TryParse<Muallimi.Domain.Shared.Subject>(request.Subject, ignoreCase: true, out var sub))
        query = query.Where(l => l.Subject == sub);

    var lessons = await query.ToListAsync();
    if (lessons.Count == 0)
        return Results.BadRequest(new { error = "No eligible lessons found for the given scope." });

    var defaultScope = System.Text.Json.JsonSerializer.Serialize(
        new[] { "TextSummary", "Audio", "Visual", "QuizItem", "QaCacheEntry" });

    var jobs = new List<Muallimi.Domain.Content.GenerationJob>();
    foreach (var lesson in lessons)
    {
        // Check if an active generation job already exists for this lesson
        var existingJob = await db.GenerationJobs
            .Where(j => j.LessonId == lesson.LessonId
                && (j.Status == Muallimi.Domain.Shared.GenerationJobStatus.Queued
                    || j.Status == Muallimi.Domain.Shared.GenerationJobStatus.Running))
            .FirstOrDefaultAsync();

        if (existingJob is not null) continue; // idempotency — skip already-queued lessons

        // Also skip lessons that already have all approved assets
        var approvedCount = await db.GeneratedAssets
            .Where(a => a.LessonId == lesson.LessonId && a.Status == Muallimi.Domain.Shared.AssetStatus.Approved)
            .CountAsync();
        if (approvedCount >= 5) continue; // all 5 asset types already approved

        var job = Muallimi.Domain.Content.GenerationJob.Create(lesson.LessonId, defaultScope, correlationId);
        lesson.MarkGenerating();
        db.GenerationJobs.Add(job);
        jobs.Add(job);
    }

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "content",
        Action = "generation-batch-triggered",
        TargetType = "GenerationJob",
        TargetId = $"batch:{jobs.Count}",
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Created("/admin/content/jobs", new
    {
        jobs_created = jobs.Count,
        job_ids = jobs.Select(j => j.JobId),
        correlation_id = correlationId
    });
})
.WithName("GenerateBatch")
.WithTags("Content");

// POST /admin/content/generate/{lessonId} — trigger generation for a single lesson
app.MapPost("/admin/content/generate/{lessonId:guid}", async (
    Guid lessonId,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var lesson = await db.Lessons.FindAsync(lessonId);
    if (lesson is null)
        return Results.NotFound(new { error = "Lesson not found." });

    if (lesson.Status != Muallimi.Domain.Shared.LessonStatus.Ingested)
        return Results.BadRequest(new { error = $"Lesson is in '{lesson.Status}' status; must be 'Ingested' to trigger generation." });

    // Idempotency — check for existing active generation job
    var existingJob = await db.GenerationJobs
        .Where(j => j.LessonId == lessonId
            && (j.Status == Muallimi.Domain.Shared.GenerationJobStatus.Queued
                || j.Status == Muallimi.Domain.Shared.GenerationJobStatus.Running))
        .FirstOrDefaultAsync();

    if (existingJob is not null)
        return Results.Conflict(new { error = "A generation job is already active for this lesson.", job_id = existingJob.JobId });

    var scope = System.Text.Json.JsonSerializer.Serialize(
        new[] { "TextSummary", "Audio", "Visual", "QuizItem", "QaCacheEntry" });

    var job = Muallimi.Domain.Content.GenerationJob.Create(lessonId, scope, correlationId);
    lesson.MarkGenerating();

    db.GenerationJobs.Add(job);
    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "content",
        Action = "generation-triggered",
        TargetType = "GenerationJob",
        TargetId = job.JobId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Created($"/admin/content/jobs/{job.JobId}", new
    {
        job_id = job.JobId,
        lesson_id = lessonId,
        status = job.Status.ToString(),
        correlation_id = correlationId
    });
})
.WithName("GenerateSingleLesson")
.WithTags("Content");

// T052: GET /admin/content/jobs/{jobId} — generation job status
app.MapGet("/admin/content/jobs/{jobId:guid}", async (Guid jobId, MuallimiDbContext db) =>
{
    var job = await db.GenerationJobs.FindAsync(jobId);
    if (job is null)
        return Results.NotFound(new { error = "Generation job not found." });

    // Get associated assets for this job
    var assets = await db.GeneratedAssets
        .Where(a => a.LessonId == job.LessonId)
        .Select(a => new
        {
            asset_id = a.AssetId,
            asset_type = a.AssetType.ToString(),
            visual_format = a.VisualFormat != null ? a.VisualFormat.ToString() : null,
            status = a.Status.ToString(),
            storage_key = a.StorageKey,
            produced_at = a.ProducedAt
        })
        .ToListAsync();

    return Results.Ok(new
    {
        job_id = job.JobId,
        lesson_id = job.LessonId,
        scope = job.Scope,
        stages = job.Stages,
        status = job.Status.ToString(),
        attempts = job.Attempts,
        started_at = job.StartedAt,
        completed_at = job.CompletedAt,
        error_reason = job.ErrorReason,
        cost_summary = job.CostSummary,
        correlation_id = job.CorrelationId,
        assets
    });
})
.WithName("GetGenerationJobStatus")
.WithTags("Content");

// ── T063: Internal Generation Result Handler ──
// Called by document-ingestion worker to report generation results

app.MapPut("/internal/generation/jobs/{jobId:guid}/status", async (
    Guid jobId, GenerationJobStatusUpdate update, MuallimiDbContext db) =>
{
    var job = await db.GenerationJobs.FindAsync(jobId);
    if (job is null)
        return Results.NotFound();

    if (update.Stages is not null)
        job.UpdateStages(System.Text.Json.JsonSerializer.Serialize(update.Stages));

    if (update.Status == "running" && job.Status == Muallimi.Domain.Shared.GenerationJobStatus.Queued)
        job.MarkRunning();
    else if (update.Status == "completed")
        job.MarkCompleted(update.CostSummary);
    else if (update.Status == "failed")
        job.MarkFailed(update.ErrorReason ?? "Unknown error");
    else if (update.Status == "partial_failed")
        job.MarkPartialFailed(update.ErrorReason ?? "Partial failure", update.CostSummary);

    await db.SaveChangesAsync();
    return Results.Ok();
})
.WithName("UpdateGenerationJobStatus")
.WithTags("Internal");

app.MapPost("/internal/generation/results", async (
    GenerationResultPayload payload, MuallimiDbContext db, AuditEventEmitter audit) =>
{
    var job = await db.GenerationJobs.FindAsync(payload.JobId);
    if (job is null)
        return Results.NotFound(new { error = "Generation job not found." });

    var lesson = await db.Lessons.FindAsync(job.LessonId);

    foreach (var assetData in payload.Assets)
    {
        if (!Enum.TryParse<Muallimi.Domain.Shared.AssetType>(assetData.AssetType, ignoreCase: true, out var assetType))
            continue;

        // Check for idempotency — don't create duplicate assets
        var existing = await db.GeneratedAssets
            .Where(a => a.LessonId == job.LessonId && a.AssetType == assetType
                && a.Version == assetData.Version)
            .FirstOrDefaultAsync();

        if (existing is not null) continue;

        Muallimi.Domain.Shared.VisualFormat? vfParsed = null;
        if (assetData.VisualFormat is not null &&
            Enum.TryParse<Muallimi.Domain.Shared.VisualFormat>(assetData.VisualFormat, ignoreCase: true, out var vfResult))
            vfParsed = vfResult;

        var asset = Muallimi.Domain.Content.GeneratedAsset.Create(
            job.LessonId,
            assetType,
            vfParsed,
            assetData.Language,
            assetData.Version,
            assetData.ProducedBy);

        asset.SetStorageKey(assetData.StorageKey);
        if (assetData.Transcript is not null)
            asset.SetTranscript(assetData.Transcript);
        if (assetData.Cost is not null)
            asset.SetCost(assetData.Cost);

        // Assets arrive in Producing status from the worker; advance through auto-validation
        asset.MarkProducing();

        db.GeneratedAssets.Add(asset);
    }

    // Record format decision if present
    if (payload.FormatDecision is not null)
    {
        if (Enum.TryParse<Muallimi.Domain.Shared.VisualFormat>(
            payload.FormatDecision.SelectedFormat, ignoreCase: true, out var fmt))
        {
            var decision = Muallimi.Domain.Content.FormatDecision.Create(
                job.LessonId, fmt,
                payload.FormatDecision.RuleTriggered,
                payload.FormatDecision.LlmRefinement);
            db.FormatDecisions.Add(decision);
        }
    }

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "content",
        Action = "generation-results-received",
        TargetType = "GenerationJob",
        TargetId = payload.JobId.ToString(),
        ActorId = "generation-worker",
        TenantId = "system",
        Outcome = "succeeded",
        CorrelationId = payload.CorrelationId
    });

    return Results.Ok(new
    {
        job_id = payload.JobId,
        assets_created = payload.Assets.Count
    });
})
.WithName("ReceiveGenerationResults")
.WithTags("Internal");

// ── T078: Internal Auto-Validation Result Handler ──
// Called by document-ingestion worker after Tier 1 auto-validation completes

app.MapPost("/internal/validation/results", async (
    AutoValidationPayload payload, MuallimiDbContext db, AuditEventEmitter audit) =>
{
    if (!Guid.TryParse(payload.AssetId, out var assetId))
        return Results.BadRequest(new { error = "Invalid asset_id." });

    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    // Parse decision
    var decision = payload.Decision?.ToLowerInvariant() == "passed"
        ? Muallimi.Domain.Shared.AutoValidationDecision.Passed
        : Muallimi.Domain.Shared.AutoValidationDecision.Failed;

    // Create AutoValidationResult
    var result = Muallimi.Domain.Content.AutoValidationResult.Create(
        assetId,
        payload.Checks ?? "{}",
        payload.GroundingEvidence ?? "[]",
        payload.ArabicQuality,
        payload.Rendering,
        payload.NarrationSync,
        payload.Accessibility,
        payload.Alignment,
        decision);

    db.AutoValidationResults.Add(result);

    // Transition asset state based on decision
    if (asset.Status == Muallimi.Domain.Shared.AssetStatus.Producing)
        asset.MarkAutoValidating();

    if (decision == Muallimi.Domain.Shared.AutoValidationDecision.Passed)
    {
        asset.MarkPendingAdminReview();

        // Record auto-validation decision
        var autoDecision = Muallimi.Domain.Review.ReviewDecision.CreateAutoValidation(
            assetId, Muallimi.Domain.Shared.ReviewOutcome.Approved, payload.CorrelationId);
        db.ReviewDecisions.Add(autoDecision);
    }
    else
    {
        asset.MarkAutoFailed();

        audit.Emit(new AuditEvent
        {
            EventCategory = "review",
            Action = "auto-validation-failed",
            TargetType = "GeneratedAsset",
            TargetId = assetId.ToString(),
            ActorId = "system",
            TenantId = "system",
            Outcome = "failed",
            CorrelationId = payload.CorrelationId ?? ""
        });
    }

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        result_id = result.ResultId,
        asset_id = assetId,
        decision = decision.ToString(),
        asset_status = asset.Status.ToString()
    });
})
.WithName("ReceiveAutoValidationResult")
.WithTags("Internal");

// ── T079: Admin Review Queue Endpoints ──

// GET /admin/review/queue — admin review queue with filters and queue-age
app.MapGet("/admin/review/queue", async (
    HttpContext httpContext,
    MuallimiDbContext db,
    string? curriculumType,
    string? grade,
    string? subject,
    string? assetType,
    int page = 1,
    int pageSize = 20) =>
{
    var query = db.GeneratedAssets
        .Where(a => a.Status == Muallimi.Domain.Shared.AssetStatus.PendingAdminReview);

    // Apply filters via lesson join if needed
    if (!string.IsNullOrEmpty(curriculumType) || !string.IsNullOrEmpty(grade) || !string.IsNullOrEmpty(subject))
    {
        var lessonQuery = db.Lessons.AsQueryable();
        if (!string.IsNullOrEmpty(curriculumType) &&
            Enum.TryParse<Muallimi.Domain.Shared.CurriculumType>(curriculumType, ignoreCase: true, out var ct))
            lessonQuery = lessonQuery.Where(l => l.CurriculumType == ct);
        if (!string.IsNullOrEmpty(grade) &&
            Enum.TryParse<Muallimi.Domain.Shared.Grade>(grade, ignoreCase: true, out var gr))
            lessonQuery = lessonQuery.Where(l => l.Grade == gr);
        if (!string.IsNullOrEmpty(subject) &&
            Enum.TryParse<Muallimi.Domain.Shared.Subject>(subject, ignoreCase: true, out var sub))
            lessonQuery = lessonQuery.Where(l => l.Subject == sub);

        var lessonIds = await lessonQuery.Select(l => l.LessonId).ToListAsync();
        query = query.Where(a => lessonIds.Contains(a.LessonId));
    }

    if (!string.IsNullOrEmpty(assetType) &&
        Enum.TryParse<Muallimi.Domain.Shared.AssetType>(assetType, ignoreCase: true, out var at))
        query = query.Where(a => a.AssetType == at);

    var total = await query.CountAsync();
    var items = await query
        .OrderBy(a => a.ProducedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(a => new
        {
            asset_id = a.AssetId,
            lesson_id = a.LessonId,
            asset_type = a.AssetType.ToString(),
            visual_format = a.VisualFormat != null ? a.VisualFormat.ToString() : null,
            status = a.Status.ToString(),
            produced_at = a.ProducedAt,
            queue_age_hours = Math.Round((DateTime.UtcNow - a.ProducedAt).TotalHours, 1),
            language = a.Language
        })
        .ToListAsync();

    return Results.Ok(new { total, page, page_size = pageSize, items });
})
.WithName("GetAdminReviewQueue")
.WithTags("Review");

// GET /admin/review/{assetId} — full asset detail with validation results and source chunks
app.MapGet("/admin/review/{assetId:guid}", async (Guid assetId, MuallimiDbContext db) =>
{
    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    var validationResult = await db.AutoValidationResults
        .Where(r => r.AssetId == assetId)
        .OrderByDescending(r => r.ValidatedAt)
        .FirstOrDefaultAsync();

    var chunks = await db.ContentChunks
        .Where(c => c.LessonId == asset.LessonId)
        .OrderBy(c => c.Sequence)
        .Select(c => new { chunk_id = c.ChunkId, text = c.Text, source_refs = c.SourceRefs })
        .ToListAsync();

    var decisions = await db.ReviewDecisions
        .Where(d => d.AssetId == assetId)
        .OrderByDescending(d => d.DecidedAt)
        .Select(d => new
        {
            decision_id = d.DecisionId,
            tier = d.Tier.ToString(),
            actor_id = d.ActorId,
            outcome = d.Outcome.ToString(),
            scope = d.Scope,
            fix_instruction = d.FixInstruction,
            decided_at = d.DecidedAt
        })
        .ToListAsync();

    return Results.Ok(new
    {
        asset_id = asset.AssetId,
        lesson_id = asset.LessonId,
        asset_type = asset.AssetType.ToString(),
        visual_format = asset.VisualFormat?.ToString(),
        status = asset.Status.ToString(),
        storage_key = asset.StorageKey,
        transcript = asset.Transcript,
        language = asset.Language,
        version = asset.Version,
        produced_at = asset.ProducedAt,
        auto_validation = validationResult is not null ? new
        {
            result_id = validationResult.ResultId,
            checks = validationResult.Checks,
            decision = validationResult.Decision.ToString(),
            validated_at = validationResult.ValidatedAt
        } : null,
        source_chunks = chunks,
        review_decisions = decisions
    });
})
.WithName("GetAssetDetail")
.WithTags("Review");

// ── T080: POST /admin/review/{assetId}/regenerate — request regeneration with stage scope ──

app.MapPost("/admin/review/{assetId:guid}/regenerate", async (
    Guid assetId,
    RegenerateRequest? request,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    // Admin can regenerate from pending_admin_review (sends back to queue)
    if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingAdminReview
        && asset.Status != Muallimi.Domain.Shared.AssetStatus.AutoFailed)
        return Results.BadRequest(new { error = $"Cannot regenerate from status '{asset.Status}'." });

    // Reset for regeneration
    asset.ResetForRegeneration(asset.Version + 1);

    // Record regeneration request as a decision
    var decision = Muallimi.Domain.Review.ReviewDecision.CreateAdminDecision(
        assetId, Muallimi.Domain.Shared.ReviewOutcome.Rejected, actor, correlationId);
    db.ReviewDecisions.Add(decision);

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "regeneration-requested",
        TargetType = "GeneratedAsset",
        TargetId = assetId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId,
        Reason = request?.Stage
    });

    return Results.Ok(new
    {
        asset_id = assetId,
        new_status = asset.Status.ToString(),
        new_version = asset.Version,
        stage = request?.Stage
    });
})
.WithName("RegenerateAsset")
.WithTags("Review");

// ── T081: PATCH /admin/review/{assetId}/submit — submit asset to expert review ──

app.MapMethods("/admin/review/{assetId:guid}/submit", new[] { "PATCH" }, async (
    Guid assetId,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingAdminReview)
        return Results.BadRequest(new { error = $"Cannot submit to expert review from status '{asset.Status}'." });

    asset.MarkPendingExpertReview();

    var decision = Muallimi.Domain.Review.ReviewDecision.CreateAdminDecision(
        assetId, Muallimi.Domain.Shared.ReviewOutcome.Approved, actor, correlationId);
    db.ReviewDecisions.Add(decision);

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "submitted-to-expert",
        TargetType = "GeneratedAsset",
        TargetId = assetId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new
    {
        asset_id = assetId,
        status = asset.Status.ToString(),
        submitted_at = DateTime.UtcNow
    });
})
.WithName("SubmitToExpert")
.WithTags("Review");

// POST /admin/review/batch/submit — submit multiple assets to expert review
app.MapPost("/admin/review/batch/submit", async (
    BatchSubmitRequest request,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var submitted = new List<Guid>();
    var errors = new List<object>();

    foreach (var id in request.AssetIds)
    {
        var asset = await db.GeneratedAssets.FindAsync(id);
        if (asset is null)
        {
            errors.Add(new { asset_id = id, error = "Not found." });
            continue;
        }
        if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingAdminReview)
        {
            errors.Add(new { asset_id = id, error = $"Invalid status: {asset.Status}." });
            continue;
        }

        asset.MarkPendingExpertReview();
        var decision = Muallimi.Domain.Review.ReviewDecision.CreateAdminDecision(
            id, Muallimi.Domain.Shared.ReviewOutcome.Approved, actor, correlationId);
        db.ReviewDecisions.Add(decision);
        submitted.Add(id);
    }

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "batch-submitted-to-expert",
        TargetType = "GeneratedAsset",
        TargetId = $"batch:{submitted.Count}",
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new { submitted_count = submitted.Count, submitted, errors });
})
.WithName("BatchSubmitToExpert")
.WithTags("Review");

// ── T082: Expert Assignment Endpoints ──

// POST /admin/review/{assetId}/assign — assign to a subject expert
app.MapPost("/admin/review/{assetId:guid}/assign", async (
    Guid assetId,
    AssignExpertRequest request,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingExpertReview)
        return Results.BadRequest(new { error = $"Cannot assign expert from status '{asset.Status}'." });

    // Look up lesson to get subject for expert-subject match
    var lesson = await db.Lessons.FindAsync(asset.LessonId);
    if (lesson is null)
        return Results.NotFound(new { error = "Lesson not found." });

    // Parse expert subject (for validation)
    if (!Enum.TryParse<Muallimi.Domain.Shared.Subject>(request.ExpertSubject, ignoreCase: true, out var expertSubject))
        return Results.BadRequest(new { error = $"Invalid expert_subject '{request.ExpertSubject}'." });

    Muallimi.Domain.Review.ReviewAssignment assignment;
    try
    {
        assignment = Muallimi.Domain.Review.ReviewAssignment.CreateExpertAssignment(
            assetId, request.ExpertId, actor,
            lesson.Subject, expertSubject, asset.AssetType);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    db.ReviewAssignments.Add(assignment);
    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "expert-assigned",
        TargetType = "GeneratedAsset",
        TargetId = assetId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Created($"/admin/review/expert/{request.ExpertId}/queue", new
    {
        assignment_id = assignment.AssignmentId,
        asset_id = assetId,
        expert_id = request.ExpertId,
        sla_due_at = assignment.SlaDueAt
    });
})
.WithName("AssignExpert")
.WithTags("Review");

// POST /admin/review/batch/assign — batch assign assets to expert
app.MapPost("/admin/review/batch/assign", async (
    BatchAssignExpertRequest request,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    if (!Enum.TryParse<Muallimi.Domain.Shared.Subject>(request.ExpertSubject, ignoreCase: true, out var expertSubject))
        return Results.BadRequest(new { error = $"Invalid expert_subject '{request.ExpertSubject}'." });

    var assigned = new List<Guid>();
    var errors = new List<object>();

    foreach (var id in request.AssetIds)
    {
        var asset = await db.GeneratedAssets.FindAsync(id);
        if (asset is null)
        {
            errors.Add(new { asset_id = id, error = "Not found." });
            continue;
        }
        if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingExpertReview)
        {
            errors.Add(new { asset_id = id, error = $"Invalid status: {asset.Status}." });
            continue;
        }

        var lesson = await db.Lessons.FindAsync(asset.LessonId);
        if (lesson is null)
        {
            errors.Add(new { asset_id = id, error = "Lesson not found." });
            continue;
        }

        try
        {
            var assignment = Muallimi.Domain.Review.ReviewAssignment.CreateExpertAssignment(
                id, request.ExpertId, actor,
                lesson.Subject, expertSubject, asset.AssetType);
            db.ReviewAssignments.Add(assignment);
            assigned.Add(id);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(new { asset_id = id, error = ex.Message });
        }
    }

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "batch-expert-assigned",
        TargetType = "ReviewAssignment",
        TargetId = $"batch:{assigned.Count}",
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new { assigned_count = assigned.Count, assigned, errors });
})
.WithName("BatchAssignExpert")
.WithTags("Review");

// GET /admin/review/expert/{expertId}/queue — expert's pending queue
app.MapGet("/admin/review/expert/{expertId}/queue", async (
    string expertId, MuallimiDbContext db, int page = 1, int pageSize = 20) =>
{
    var assignments = await db.ReviewAssignments
        .Where(a => a.AssignedTo == expertId
            && a.Tier == Muallimi.Domain.Shared.ReviewTier.ExpertReview
            && (a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.Open
                || a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.InReview))
        .OrderBy(a => a.SlaDueAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync();

    var items = new List<object>();
    foreach (var assignment in assignments)
    {
        var asset = await db.GeneratedAssets.FindAsync(assignment.AssetId);
        items.Add(new
        {
            assignment_id = assignment.AssignmentId,
            asset_id = assignment.AssetId,
            asset_type = asset?.AssetType.ToString(),
            visual_format = asset?.VisualFormat?.ToString(),
            status = assignment.Status.ToString(),
            assigned_at = assignment.AssignedAt,
            sla_due_at = assignment.SlaDueAt,
            is_overdue = assignment.IsOverdue,
            lesson_id = asset?.LessonId,
            language = asset?.Language
        });
    }

    return Results.Ok(new { expert_id = expertId, total = items.Count, items });
})
.WithName("GetExpertQueue")
.WithTags("Review");

// ── T083: PATCH /admin/review/{assetId}/approve — expert approve + publish ──

app.MapMethods("/admin/review/{assetId:guid}/approve", new[] { "PATCH" }, async (
    Guid assetId,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingExpertReview)
        return Results.BadRequest(new { error = $"Cannot approve from status '{asset.Status}'." });

    // T085: Concurrent approval guard — check if already approved
    var existingApproval = await db.ReviewDecisions
        .Where(d => d.AssetId == assetId
            && d.Tier == Muallimi.Domain.Shared.ReviewTier.ExpertReview
            && d.Outcome == Muallimi.Domain.Shared.ReviewOutcome.Approved)
        .AnyAsync();

    if (existingApproval)
        return Results.Conflict(new { error = "Asset has already been approved by another expert." });

    // Approve the asset
    asset.MarkApproved();

    // Record expert decision
    var decision = Muallimi.Domain.Review.ReviewDecision.CreateExpertDecision(
        assetId, Muallimi.Domain.Shared.ReviewOutcome.Approved, actor,
        null, null, correlationId);
    db.ReviewDecisions.Add(decision);

    // Find the admin who submitted this asset to get the full audit trail
    var adminDecision = await db.ReviewDecisions
        .Where(d => d.AssetId == assetId && d.Tier == Muallimi.Domain.Shared.ReviewTier.AdminReview
            && d.Outcome == Muallimi.Domain.Shared.ReviewOutcome.Approved)
        .OrderByDescending(d => d.DecidedAt)
        .FirstOrDefaultAsync();

    var adminActor = adminDecision?.ActorId ?? "unknown";

    // Create PublishedAsset with deterministic ID
    var runtimeUrl = $"/content/{asset.LessonId}/{asset.AssetType.ToString().ToLowerInvariant()}/{asset.AssetId}";

    var published = Muallimi.Domain.Publication.PublishedAsset.Create(
        asset.AssetId,
        asset.LessonId,
        asset.AssetType,
        asset.VisualFormat,
        runtimeUrl,
        adminActor,
        actor,
        asset.Version);

    db.PublishedAssets.Add(published);

    // Update CoverageStatus
    var coverage = await db.CoverageStatuses.FindAsync(asset.LessonId, asset.AssetType);
    if (coverage is not null)
    {
        coverage.State = Muallimi.Domain.Shared.CoverageState.Approved;
        coverage.LastUpdatedAt = DateTime.UtcNow;
    }
    else
    {
        db.CoverageStatuses.Add(new Muallimi.Domain.Coverage.CoverageStatus
        {
            LessonId = asset.LessonId,
            AssetType = asset.AssetType,
            State = Muallimi.Domain.Shared.CoverageState.Approved,
            LastUpdatedAt = DateTime.UtcNow
        });
    }

    // Close the expert assignment
    var assignment = await db.ReviewAssignments
        .Where(a => a.AssetId == assetId
            && a.Tier == Muallimi.Domain.Shared.ReviewTier.ExpertReview
            && (a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.Open
                || a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.InReview))
        .FirstOrDefaultAsync();
    assignment?.MarkSubmitted();

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "expert-approved",
        TargetType = "GeneratedAsset",
        TargetId = assetId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId
    });

    return Results.Ok(new
    {
        asset_id = assetId,
        status = asset.Status.ToString(),
        published_id = published.PublishedId,
        runtime_url = published.RuntimeUrl,
        approved_by_admin = adminActor,
        approved_by_expert = actor,
        approved_at = published.ApprovedAt
    });
})
.WithName("ExpertApprove")
.WithTags("Review");

// ── T084: PATCH /admin/review/{assetId}/reject and /request-edit ──

app.MapMethods("/admin/review/{assetId:guid}/reject", new[] { "PATCH" }, async (
    Guid assetId,
    RejectRequest request,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    if (string.IsNullOrWhiteSpace(request.FixInstruction))
        return Results.BadRequest(new { error = "fix_instruction is required for rejection." });

    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingExpertReview)
        return Results.BadRequest(new { error = $"Cannot reject from status '{asset.Status}'." });

    asset.MarkRejected();

    Muallimi.Domain.Review.ReviewDecision decision;
    try
    {
        decision = Muallimi.Domain.Review.ReviewDecision.CreateExpertDecision(
            assetId, Muallimi.Domain.Shared.ReviewOutcome.Rejected, actor,
            request.FixInstruction, null, correlationId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    db.ReviewDecisions.Add(decision);

    // Close the expert assignment
    var assignment = await db.ReviewAssignments
        .Where(a => a.AssetId == assetId
            && a.Tier == Muallimi.Domain.Shared.ReviewTier.ExpertReview
            && (a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.Open
                || a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.InReview))
        .FirstOrDefaultAsync();
    assignment?.MarkSubmitted();

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "expert-rejected",
        TargetType = "GeneratedAsset",
        TargetId = assetId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "rejected",
        CorrelationId = correlationId,
        Reason = request.FixInstruction
    });

    return Results.Ok(new
    {
        asset_id = assetId,
        status = asset.Status.ToString(),
        fix_instruction = request.FixInstruction,
        decided_at = decision.DecidedAt
    });
})
.WithName("ExpertReject")
.WithTags("Review");

app.MapMethods("/admin/review/{assetId:guid}/request-edit", new[] { "PATCH" }, async (
    Guid assetId,
    RequestEditRequest request,
    HttpContext httpContext,
    MuallimiDbContext db,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    if (string.IsNullOrWhiteSpace(request.FixInstruction))
        return Results.BadRequest(new { error = "fix_instruction is required for edit requests." });
    if (string.IsNullOrWhiteSpace(request.Stage))
        return Results.BadRequest(new { error = "stage is required for edit requests." });

    var asset = await db.GeneratedAssets.FindAsync(assetId);
    if (asset is null)
        return Results.NotFound(new { error = "Asset not found." });

    if (asset.Status != Muallimi.Domain.Shared.AssetStatus.PendingExpertReview)
        return Results.BadRequest(new { error = $"Cannot request edit from status '{asset.Status}'." });

    asset.MarkEditRequested();

    Muallimi.Domain.Review.ReviewDecision decision;
    try
    {
        decision = Muallimi.Domain.Review.ReviewDecision.CreateExpertDecision(
            assetId, Muallimi.Domain.Shared.ReviewOutcome.EditRequested, actor,
            request.FixInstruction, request.Stage, correlationId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    db.ReviewDecisions.Add(decision);

    // Close the expert assignment
    var assignment = await db.ReviewAssignments
        .Where(a => a.AssetId == assetId
            && a.Tier == Muallimi.Domain.Shared.ReviewTier.ExpertReview
            && (a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.Open
                || a.Status == Muallimi.Domain.Shared.ReviewAssignmentStatus.InReview))
        .FirstOrDefaultAsync();
    assignment?.MarkSubmitted();

    await db.SaveChangesAsync();

    audit.Emit(new AuditEvent
    {
        EventCategory = "review",
        Action = "expert-edit-requested",
        TargetType = "GeneratedAsset",
        TargetId = assetId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "edit-requested",
        CorrelationId = correlationId,
        Reason = $"[{request.Stage}] {request.FixInstruction}"
    });

    return Results.Ok(new
    {
        asset_id = assetId,
        status = asset.Status.ToString(),
        stage = request.Stage,
        fix_instruction = request.FixInstruction,
        decided_at = decision.DecidedAt
    });
})
.WithName("ExpertRequestEdit")
.WithTags("Review");

// ── US5: Invalidation Endpoint ──
//
// The POST /admin/curriculum/{sourceId}/update endpoint (delta re-upload) was
// removed for the MVP stage-separated flow. To replace a botched curriculum,
// admins delete the existing source from the Documents page and upload a
// fresh copy. A re-introduced "update in place" workflow will land later as a
// per-document action, not as a parallel upload mode.

// T107: PATCH /admin/content/{assetId}/invalidate — manually invalidate a live asset
app.MapMethods("/admin/content/{assetId:guid}/invalidate", new[] { "PATCH" }, async (
    Guid assetId,
    InvalidateRequest request,
    HttpContext httpContext,
    AssetInvalidationHandler invalidationHandler,
    AuditEventEmitter audit) =>
{
    var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
    var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

    if (string.IsNullOrWhiteSpace(request.Reason))
        return Results.BadRequest(new { error = "reason is required for invalidation." });

    SingleAssetInvalidationResult result;
    try
    {
        result = await invalidationHandler.InvalidateSingleAssetAsync(
            assetId, actor, request.Reason, correlationId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    audit.Emit(new AuditEvent
    {
        EventCategory = "content",
        Action = "asset-invalidated",
        TargetType = "GeneratedAsset",
        TargetId = assetId.ToString(),
        ActorId = actor,
        TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
        Outcome = "succeeded",
        CorrelationId = correlationId,
        Reason = request.Reason
    });

    return Results.Ok(new
    {
        asset_id = result.AssetId,
        lesson_id = result.LessonId,
        asset_type = result.AssetType,
        lesson_status = result.LessonStatus,
        reason = request.Reason,
        invalidated_at = DateTime.UtcNow
    });
})
.WithName("InvalidateAsset")
.WithTags("Content");

// T105: Internal endpoint — lookup existing lesson hashes for delta comparison by ingestion worker
app.MapGet("/internal/lessons/hashes", async (
    Guid sourceId, MuallimiDbContext db) =>
{
    // Find the latest structure for this source
    var structure = await db.CurriculumStructures
        .Where(s => s.SourceId == sourceId)
        .OrderByDescending(s => s.ExtractedAt)
        .FirstOrDefaultAsync();

    if (structure is null)
        return Results.Ok(new { lessons = Array.Empty<object>() });

    var lessons = await db.Lessons
        .Where(l => l.StructureId == structure.StructureId)
        .Select(l => new
        {
            lesson_id = l.LessonId,
            path = l.Path,
            content_hash = l.ContentHash
        })
        .ToListAsync();

    return Results.Ok(new { source_id = sourceId, lessons });
})
.WithName("GetLessonHashes")
.WithTags("Internal");

// ── US4: Runtime Retrieval Endpoints ──
app.MapRetrievalEndpoints();

// ── US6: Coverage dashboard ──
app.MapCoverageEndpoints();

// ── Phase 2 US1: Tutor exposure facade ──
app.MapTutorExposureEndpoints();

// ── Phase 2 US3: AI tutor routing configuration admin surface ──
app.MapRoutingConfigurationEndpoints();

// ── Phase 2 US4 (T078, T082): Prompt registry CRUD + audit + incident lookup ──
app.MapPromptRegistryEndpoints();
app.MapIncidentLookupEndpoint();

// ── Phase 2 US5 (T090): Provider binding admin surface ──
app.MapProviderBindingEndpoints();

// ── Phase 2 US6 (T102): AI operations query surface (requests / metrics / refusals / readiness) ──
app.MapAiOperationsEndpoints();
app.MapPhase6AiOperationsEndpoints();

// ── Phase 6 US4 (T078/T081): Distributed trace + incident management ──
app.MapDistributedTracingEndpoints();
app.MapIncidentManagementEndpoints();

// ── Phase 6 US6 (T100-T102): Operator platform management + tenant health ──
Muallimi.Api.OperatorManagement.OperatorEndpoints.MapOperatorManagementEndpoints(app);
Muallimi.Api.OperatorManagement.LaunchReadinessGate.LaunchReadinessGateEndpoints.MapLaunchReadinessGateEndpoints(app);

// ── Phase 2 US7 (T114/T117): Red-team results query surface ──
app.MapRedTeamResultsEndpoints();

// ── Phase 6 US8 (T119): seed default retention policies if missing ──
using (var seedScope = app.Services.CreateScope())
{
    try
    {
        var db = seedScope.ServiceProvider.GetRequiredService<Muallimi.Infrastructure.Persistence.MuallimiDbContext>();
        await Muallimi.Api.Compliance.DataRetention.DefaultRetentionPolicySeeder.EnsureSeededAsync(db);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "data_retention.seed_skipped");
    }
}

// ── Phase 9 (T047): seed Platform tenant + 8 system roles ──
try
{
    var identitySeeds = app.Services.GetRequiredService<IdentitySeedRunner>();
    await identitySeeds.RunAsync();
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "identity.seed_skipped");
}

// ── Phase 6 Billing: seed the 3 canonical family plans (idempotent) ──
try
{
    using var billingSeedScope = app.Services.CreateScope();
    var planSeeder = billingSeedScope.ServiceProvider
        .GetRequiredService<Muallimi.Infrastructure.Billing.Seed.SubscriptionPlanSeeder>();
    var inserted = await planSeeder.EnsureSeededAsync();
    if (inserted > 0)
    {
        app.Logger.LogInformation("billing.plan_seed: {Count} family plan(s) inserted.", inserted);
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "billing.plan_seed_skipped");
}

app.Run();

// ── Request DTOs for internal endpoints ──

record StructureUpdatePayload(
    [property: System.Text.Json.Serialization.JsonPropertyName("nodes")] string Nodes);

record IngestionJobStatusUpdate(
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("stages")] object[]? Stages,
    [property: System.Text.Json.Serialization.JsonPropertyName("error_reason")] string? ErrorReason,
    [property: System.Text.Json.Serialization.JsonPropertyName("correlation_id")] string? CorrelationId,
    // True when the worker is processing an MVP extract-only job. On completion
    // the source is parked in SourceStatus.Extracted instead of advancing all
    // the way to Indexed, since chunk + embed are deferred to a later trigger.
    [property: System.Text.Json.Serialization.JsonPropertyName("extract_only")] bool ExtractOnly = false);

record IngestionResultPayload(
    [property: System.Text.Json.Serialization.JsonPropertyName("source_id")] Guid SourceId,
    [property: System.Text.Json.Serialization.JsonPropertyName("job_id")] Guid JobId,
    [property: System.Text.Json.Serialization.JsonPropertyName("correlation_id")] string CorrelationId,
    [property: System.Text.Json.Serialization.JsonPropertyName("structure_nodes")] string StructureNodes,
    [property: System.Text.Json.Serialization.JsonPropertyName("lessons")] List<IngestionLessonPayload> Lessons,
    // When true the payload represents an MVP extract-only result: structure
    // tree + lesson skeletons only, no chunks, no embeddings.
    [property: System.Text.Json.Serialization.JsonPropertyName("extract_only")] bool ExtractOnly = false);

record IngestionLessonPayload(
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
    [property: System.Text.Json.Serialization.JsonPropertyName("content_hash")] string ContentHash,
    [property: System.Text.Json.Serialization.JsonPropertyName("chunks")] List<IngestionChunkPayload> Chunks);

record IngestionChunkPayload(
    [property: System.Text.Json.Serialization.JsonPropertyName("sequence")] int Sequence,
    [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_count")] int TokenCount,
    [property: System.Text.Json.Serialization.JsonPropertyName("overlap_with_previous")] int OverlapWithPrevious,
    [property: System.Text.Json.Serialization.JsonPropertyName("math_blocks")] string MathBlocks,
    [property: System.Text.Json.Serialization.JsonPropertyName("source_refs")] string SourceRefs,
    [property: System.Text.Json.Serialization.JsonPropertyName("metadata")] string Metadata,
    [property: System.Text.Json.Serialization.JsonPropertyName("embedding")] float[]? Embedding,
    [property: System.Text.Json.Serialization.JsonPropertyName("embedding_model_version")] string? EmbeddingModelVersion);

// ── Generation DTOs ──

record GenerationBatchRequest(
    string? CurriculumType,
    string? Grade,
    string? Subject);

record GenerationJobStatusUpdate(
    string Status,
    object[]? Stages,
    string? ErrorReason,
    string? CostSummary);

record GenerationResultPayload(
    Guid JobId,
    string CorrelationId,
    List<GenerationAssetPayload> Assets,
    FormatDecisionPayload? FormatDecision);

record GenerationAssetPayload(
    string AssetType,
    string? VisualFormat,
    string StorageKey,
    string? Transcript,
    string Language,
    int Version,
    string ProducedBy,
    string? Cost);

record FormatDecisionPayload(
    string SelectedFormat,
    string RuleTriggered,
    string? LlmRefinement);

// ── Review DTOs (US3) ──

record AutoValidationPayload(
    string AssetId,
    string? Checks,
    string? GroundingEvidence,
    string? ArabicQuality,
    string? Rendering,
    string? NarrationSync,
    string? Accessibility,
    string? Alignment,
    string? Decision,
    string? CorrelationId);

record RegenerateRequest(string? Stage);

record BatchSubmitRequest(List<Guid> AssetIds);

record AssignExpertRequest(string ExpertId, string ExpertSubject);

record BatchAssignExpertRequest(List<Guid> AssetIds, string ExpertId, string ExpertSubject);

record RejectRequest(string FixInstruction);

record RequestEditRequest(string FixInstruction, string Stage);

// ── US5 DTOs (Update & Invalidation) ──

record InvalidateRequest(string Reason);
