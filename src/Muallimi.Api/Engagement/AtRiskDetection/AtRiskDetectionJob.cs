using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Api.Engagement.FocusAreas;
using Muallimi.Api.Engagement.InterventionPrompts;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T146 (US8) — AtRiskDetectionJob.
///
/// Orchestrates a single tick of the at-risk pipeline:
///   1. Look up the active flag (if any) for the student.
///   2. Run the <see cref="IAtRiskEvaluator"/> against the current
///      <see cref="AtRiskThresholdSet"/>.
///   3. If the threshold is exceeded and no active flag exists, raise a
///      new <see cref="AtRiskFlag"/>, generate an
///      <see cref="InterventionPrompt"/> through the Phase 2 guardrail
///      chain, link them, and emit <c>at_risk_flagged</c>.
///   4. If the threshold is met by an active flag's recovery signals,
///      clear the flag and emit <c>at_risk_cleared</c>.
///   5. Otherwise no-op so re-runs on the same data are idempotent.
///
/// Background mode is disabled by default; integration tests + the local
/// smoke script drive <see cref="EvaluateStudentAsync"/> directly.
/// </summary>
public sealed class AtRiskDetectionJobOptions
{
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);
}

public sealed record AtRiskDetectionOutcome(
    bool Raised,
    bool Cleared,
    Guid? AtRiskFlagId,
    Guid? InterventionPromptId,
    string FinalStage);

public sealed class AtRiskDetectionJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AtRiskDetectionJob> _logger;
    private readonly AtRiskDetectionJobOptions _options;

    public AtRiskDetectionJob(
        IServiceProvider services,
        ILogger<AtRiskDetectionJob> logger,
        IOptions<AtRiskDetectionJobOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AtRiskDetectionJob disabled; skipping tick loop");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AtRiskDetectionJob tick failed");
            }

            try { await Task.Delay(_options.Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAtRiskDetectionOrchestrator>();

        var today = DateTime.UtcNow.Date;
        var activeLinks = await db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.EffectiveStart <= today && (l.EffectiveEnd == null || l.EffectiveEnd >= today))
            .Select(l => new { l.TenantId, l.StudentId })
            .Distinct()
            .ToListAsync(ct);

        foreach (var link in activeLinks)
        {
            ct.ThrowIfCancellationRequested();
            var correlationId = Guid.NewGuid().ToString("D");
            try
            {
                await orchestrator.EvaluateStudentAsync(link.TenantId, link.StudentId, correlationId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AtRiskDetection failed for tenant={Tenant} student={Student}",
                    link.TenantId, link.StudentId);
            }
        }
    }
}

public interface IAtRiskDetectionOrchestrator
{
    Task<AtRiskDetectionOutcome> EvaluateStudentAsync(
        Guid tenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default);
}

public sealed class AtRiskDetectionOrchestrator : IAtRiskDetectionOrchestrator
{
    private readonly MuallimiDbContext _db;
    private readonly IAtRiskFlagRepository _flags;
    private readonly IInterventionPromptRepository _prompts;
    private readonly IAtRiskEvaluator _evaluator;
    private readonly IAtRiskThresholdCatalogue _catalogue;
    private readonly IInterventionPromptGenerator _generator;
    private readonly IFocusAreaDeepLinkValidator _deepLinks;
    private readonly IAtRiskEventEmitter _events;
    private readonly ILogger<AtRiskDetectionOrchestrator> _logger;

    public AtRiskDetectionOrchestrator(
        MuallimiDbContext db,
        IAtRiskFlagRepository flags,
        IInterventionPromptRepository prompts,
        IAtRiskEvaluator evaluator,
        IAtRiskThresholdCatalogue catalogue,
        IInterventionPromptGenerator generator,
        IFocusAreaDeepLinkValidator deepLinks,
        IAtRiskEventEmitter events,
        ILogger<AtRiskDetectionOrchestrator> logger)
    {
        _db = db;
        _flags = flags;
        _prompts = prompts;
        _evaluator = evaluator;
        _catalogue = catalogue;
        _generator = generator;
        _deepLinks = deepLinks;
        _events = events;
        _logger = logger;
    }

    public async Task<AtRiskDetectionOutcome> EvaluateStudentAsync(
        Guid tenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default)
    {
        var thresholds = _catalogue.Current;
        var evaluation = await _evaluator.EvaluateAsync(tenantId, studentId, thresholds, ct);
        var existing = await _flags.GetActiveAsync(tenantId, studentId, ct);

        if (existing is null && evaluation.ExceedsThreshold)
        {
            return await RaiseAsync(tenantId, studentId, evaluation, correlationId, ct);
        }

        if (existing is not null && evaluation.RecoveryAchieved)
        {
            return await ClearAsync(existing, ct);
        }

        return new AtRiskDetectionOutcome(
            Raised: false,
            Cleared: false,
            AtRiskFlagId: existing?.AtRiskFlagId,
            InterventionPromptId: existing?.LinkedInterventionPromptId,
            FinalStage: "noop");
    }

    private async Task<AtRiskDetectionOutcome> RaiseAsync(
        Guid tenantId,
        Guid studentId,
        AtRiskEvaluation evaluation,
        string correlationId,
        CancellationToken ct)
    {
        var flagId = Guid.NewGuid();
        var promptId = Guid.NewGuid();

        var deepLink = await ResolveDeepLinkAsync(tenantId, studentId, evaluation.Evidence, ct);

        var generation = await _generator.GenerateAsync(
            tenantId, studentId, promptId, evaluation.Evidence, deepLink, correlationId, ct);

        if (generation.FinalStage == "refuse")
        {
            // Guardrail or red-team check forced a refusal — skip the row so
            // a punitive or shaming prompt never reaches the parent.
            _logger.LogInformation(
                "AtRisk intervention prompt refused tenant={Tenant} student={Student}",
                tenantId, studentId);
            return new AtRiskDetectionOutcome(
                Raised: false,
                Cleared: false,
                AtRiskFlagId: null,
                InterventionPromptId: null,
                FinalStage: "refuse");
        }

        var nextStep = new
        {
            phase3_mode = deepLink.Phase3Mode,
            deep_link = deepLink.DeepLink,
            curriculum_node_path = deepLink.CurriculumNodePath,
        };

        var nowUtc = DateTime.UtcNow;
        var prompt = new InterventionPrompt
        {
            InterventionPromptId = promptId,
            TenantId = tenantId,
            StudentId = studentId,
            OriginatingFlagId = flagId,
            BodyAr = generation.BodyAr,
            BodyEn = generation.BodyEn,
            NextStep = JsonSerializer.Serialize(nextStep, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }),
            GuardrailDecisionTrailId = generation.GuardrailDecisionTrailId,
            CreatedAt = nowUtc,
            CorrelationId = correlationId,
        };

        var flag = new AtRiskFlag
        {
            AtRiskFlagId = flagId,
            TenantId = tenantId,
            StudentId = studentId,
            ThresholdVersion = evaluation.ThresholdVersion,
            TriggeringEvidence = evaluation.Evidence.ToJson(),
            RaisedAt = nowUtc,
            ClearedAt = null,
            LinkedInterventionPromptId = promptId,
            CorrelationId = correlationId,
        };

        await _prompts.AddAsync(prompt, ct);
        await _flags.AddAsync(flag, ct);
        await _events.EmitFlaggedAsync(flag, promptId, ct);

        await _db.SaveChangesAsync(ct);

        return new AtRiskDetectionOutcome(
            Raised: true,
            Cleared: false,
            AtRiskFlagId: flagId,
            InterventionPromptId: promptId,
            FinalStage: generation.FinalStage);
    }

    private async Task<AtRiskDetectionOutcome> ClearAsync(AtRiskFlag flag, CancellationToken ct)
    {
        await _flags.ClearAsync(flag, DateTime.UtcNow, ct);
        await _events.EmitClearedAsync(flag, ct);
        await _db.SaveChangesAsync(ct);
        return new AtRiskDetectionOutcome(
            Raised: false,
            Cleared: true,
            AtRiskFlagId: flag.AtRiskFlagId,
            InterventionPromptId: flag.LinkedInterventionPromptId,
            FinalStage: "cleared");
    }

    private async Task<FocusAreaDeepLinkValidation> ResolveDeepLinkAsync(
        Guid tenantId,
        Guid studentId,
        AtRiskTriggeringEvidence evidence,
        CancellationToken ct)
    {
        // Prefer the student's most recent valid focus-area deep link so the
        // intervention prompt always anchors to a real Phase 1 node the
        // student already touched. Fall back to a subject-level review link
        // when no focus area is on file (the deep-link validator handles the
        // graceful-degradation case).
        var focus = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId
                        && f.StudentId == studentId
                        && f.ValidUntil > DateTime.UtcNow)
            .OrderByDescending(f => f.ComputedAt)
            .FirstOrDefaultAsync(ct);

        var subjectId = focus?.SubjectId
                        ?? evidence.LowestMasterySubjectId
                        ?? Guid.Empty;
        var chapterId = focus?.ChapterId ?? Guid.Empty;
        var topicId = focus?.TopicId ?? evidence.LowestMasteryTopicId ?? Guid.Empty;

        return await _deepLinks.ValidateAsync(subjectId, chapterId, topicId, ct);
    }
}

public static class AtRiskDetectionJobServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4AtRiskDetectionOrchestrator(this IServiceCollection services)
    {
        services.AddScoped<IAtRiskDetectionOrchestrator, AtRiskDetectionOrchestrator>();
        return services;
    }

    public static IServiceCollection AddPhase4AtRiskDetectionJob(this IServiceCollection services)
    {
        services.AddSingleton<IHostedService, AtRiskDetectionJob>();
        return services;
    }
}
