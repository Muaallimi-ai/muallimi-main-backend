using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.FocusAreas;

/// <summary>
/// T110 (US5) — FocusAreaCalculator.
///
/// Pipeline:
///   1. Collect signals via <see cref="IFocusAreaSignalCollector"/>.
///   2. For each candidate, validate the deep link via
///      <see cref="IFocusAreaDeepLinkValidator"/>. Rejected nodes never
///      reach the <c>FocusArea</c> table — that satisfies the T103
///      grounding invariant.
///   3. Generate rationales via <see cref="IFocusAreaRationaleGenerator"/>.
///      A <c>refuse</c> verdict from the guardrail chain skips the row so
///      parent and student surfaces never render an un-approved rationale.
///   4. Replace the student's active focus-area set in a single unit of
///      work and emit a <c>focus_area_updated</c> downstream event per
///      retained row.
/// </summary>
public interface IFocusAreaCalculator
{
    Task<FocusAreaCalculationResult> RecomputeAsync(
        Guid tenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record FocusAreaCalculationResult(
    int CandidateCount,
    int WrittenCount,
    int RejectedByDeepLink,
    int RejectedByGuardrail);

public sealed class FocusAreaCalculator : IFocusAreaCalculator
{
    private const int MaxFocusAreasPerStudent = 5;
    private static readonly TimeSpan ValidWindow = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;
    private readonly IFocusAreaRepository _repository;
    private readonly IFocusAreaSignalCollector _signals;
    private readonly IFocusAreaDeepLinkValidator _deepLinks;
    private readonly IFocusAreaRationaleGenerator _rationales;
    private readonly IFocusAreaEventEmitter _events;
    private readonly ILogger<FocusAreaCalculator> _logger;

    public FocusAreaCalculator(
        MuallimiDbContext db,
        IFocusAreaRepository repository,
        IFocusAreaSignalCollector signals,
        IFocusAreaDeepLinkValidator deepLinks,
        IFocusAreaRationaleGenerator rationales,
        IFocusAreaEventEmitter events,
        ILogger<FocusAreaCalculator> logger)
    {
        _db = db;
        _repository = repository;
        _signals = signals;
        _deepLinks = deepLinks;
        _rationales = rationales;
        _events = events;
        _logger = logger;
    }

    public async Task<FocusAreaCalculationResult> RecomputeAsync(
        Guid tenantId,
        Guid studentId,
        string correlationId,
        CancellationToken ct = default)
    {
        var signals = await _signals.CollectAsync(tenantId, studentId, ct);
        if (signals.Count == 0)
        {
            var existing = await _repository.ListForStudentAsync(tenantId, studentId, ct);
            if (existing.Count > 0)
            {
                await _repository.RemoveRangeAsync(existing, ct);
                await _db.SaveChangesAsync(ct);
            }
            return new FocusAreaCalculationResult(0, 0, 0, 0);
        }

        var candidates = signals.Take(MaxFocusAreasPerStudent).ToList();
        var rejectedByDeepLink = 0;
        var rejectedByGuardrail = 0;
        var written = new List<FocusArea>();

        foreach (var signal in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var deepLink = await _deepLinks.ValidateAsync(signal.SubjectId, signal.ChapterId, signal.TopicId, ct);
            if (!deepLink.IsValid)
            {
                rejectedByDeepLink++;
                _logger.LogInformation(
                    "FocusArea candidate rejected by deep-link validator tenant={Tenant} student={Student} subject={Subject} topic={Topic}",
                    tenantId, studentId, signal.SubjectId, signal.TopicId);
                continue;
            }

            var focusAreaId = Guid.NewGuid();
            var rationale = await _rationales.GenerateAsync(
                tenantId, studentId, focusAreaId, signal, deepLink, correlationId, ct);

            if (rationale.FinalStage == "refuse")
            {
                rejectedByGuardrail++;
                _logger.LogInformation(
                    "FocusArea candidate rejected by guardrail chain tenant={Tenant} student={Student} focusArea={FocusArea}",
                    tenantId, studentId, focusAreaId);
                continue;
            }

            var signalSummary = new
            {
                mastery_band = signal.MasteryBand,
                mastery_gap = signal.MasteryGap,
                quiz_error_count = signal.QuizErrorCount,
                homework_help_count = signal.HomeworkHelpCount,
                touched_event_count = signal.TouchedEventCount,
                originating_progress_record_ids = signal.OriginatingProgressRecordIds,
                composite_score = signal.Score,
            };
            var nextStep = new
            {
                phase3_mode = deepLink.Phase3Mode,
                deep_link = deepLink.DeepLink,
                curriculum_node = new
                {
                    subject_id = signal.SubjectId,
                    chapter_id = signal.ChapterId,
                    topic_id = signal.TopicId,
                    path = deepLink.CurriculumNodePath,
                },
            };

            var now = DateTime.UtcNow;
            var row = new FocusArea
            {
                FocusAreaId = focusAreaId,
                TenantId = tenantId,
                StudentId = studentId,
                CurriculumType = signal.CurriculumType,
                SubjectId = signal.SubjectId,
                ChapterId = signal.ChapterId,
                TopicId = signal.TopicId,
                SignalSummary = JsonSerializer.Serialize(signalSummary, JsonOptions),
                RationaleAr = rationale.RationaleAr,
                RationaleEn = rationale.RationaleEn,
                SuggestedNextStep = JsonSerializer.Serialize(nextStep, JsonOptions),
                GuardrailDecisionTrailId = rationale.GuardrailDecisionTrailId,
                ComputedAt = now,
                ValidUntil = now.Add(ValidWindow),
                CorrelationId = correlationId,
            };
            written.Add(row);
        }

        var previous = await _repository.ListForStudentAsync(tenantId, studentId, ct);
        if (previous.Count > 0)
        {
            await _repository.RemoveRangeAsync(previous, ct);
        }
        foreach (var row in written)
        {
            await _repository.AddAsync(row, ct);
        }

        foreach (var row in written)
        {
            await _events.EmitUpdatedAsync(row, ct);
        }

        await _db.SaveChangesAsync(ct);

        return new FocusAreaCalculationResult(
            CandidateCount: signals.Count,
            WrittenCount: written.Count,
            RejectedByDeepLink: rejectedByDeepLink,
            RejectedByGuardrail: rejectedByGuardrail);
    }
}

/// <summary>
/// Emits <c>focus_area_updated</c> downstream events via
/// <see cref="IPhase4DownstreamEventOutbox"/> inside the same unit of work
/// that writes the <see cref="FocusArea"/> row.
/// </summary>
public interface IFocusAreaEventEmitter
{
    Task EmitUpdatedAsync(FocusArea focusArea, CancellationToken ct = default);
}

public sealed class FocusAreaEventEmitter : IFocusAreaEventEmitter
{
    private readonly IPhase4DownstreamEventOutbox _outbox;

    public FocusAreaEventEmitter(IPhase4DownstreamEventOutbox outbox)
    {
        _outbox = outbox;
    }

    public Task EmitUpdatedAsync(FocusArea focusArea, CancellationToken ct = default)
    {
        var scope = new
        {
            curriculum_type = focusArea.CurriculumType,
            subject_id = focusArea.SubjectId,
            chapter_id = focusArea.ChapterId,
            topic_id = focusArea.TopicId,
        };
        var payload = new
        {
            focus_area_id = focusArea.FocusAreaId,
            subject_id = focusArea.SubjectId,
            chapter_id = focusArea.ChapterId,
            topic_id = focusArea.TopicId,
            guardrail_decision_trail_id = focusArea.GuardrailDecisionTrailId,
            computed_at = focusArea.ComputedAt,
            valid_until = focusArea.ValidUntil,
        };
        return _outbox.EnqueueAsync(
            Phase4DownstreamEventKind.focus_area_updated,
            focusArea.TenantId,
            focusArea.StudentId,
            scope,
            payload,
            focusArea.CorrelationId,
            occurredAt: focusArea.ComputedAt,
            ct);
    }
}

public static class FocusAreaCalculatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4FocusAreaCalculator(this IServiceCollection services)
    {
        services.AddScoped<IFocusAreaEventEmitter, FocusAreaEventEmitter>();
        services.AddScoped<IFocusAreaCalculator, FocusAreaCalculator>();
        return services;
    }
}
