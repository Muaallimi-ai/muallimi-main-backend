using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.DownstreamEvents;

/// <summary>
/// T042 (US4) — Convenience emitter for the three ingestion-triggered
/// Phase 4 downstream events: <c>mastery_updated</c>, <c>streak_changed</c>,
/// and <c>badge_awarded</c>.
///
/// Delegates to <see cref="IPhase4DownstreamEventOutbox"/> so the outbox row
/// is written inside the same DbContext unit of work as the state change
/// that caused it. Payload shapes follow
/// <c>specs/006-engagement-progress-parent/contracts/phase4-downstream-events-contract.md</c>.
/// </summary>
public interface IPhase4DownstreamEventEmitter
{
    Task EmitMasteryUpdatedAsync(
        Guid tenantId,
        Guid studentId,
        string curriculumType,
        Guid subjectId,
        Guid? topicId,
        decimal priorScore,
        decimal newScore,
        string priorBand,
        string newBand,
        string calculationVersion,
        string correlationId,
        CancellationToken ct = default);

    Task EmitStreakChangedAsync(
        Guid tenantId,
        Guid studentId,
        int priorLength,
        int newLength,
        string familyTimezone,
        string correlationId,
        CancellationToken ct = default);

    Task EmitBadgeAwardedAsync(
        Guid tenantId,
        Guid studentId,
        BadgeAward award,
        string badgeKey,
        string correlationId,
        CancellationToken ct = default);
}

public sealed class Phase4DownstreamEventEmitter : IPhase4DownstreamEventEmitter
{
    private readonly IPhase4DownstreamEventOutbox _outbox;

    public Phase4DownstreamEventEmitter(IPhase4DownstreamEventOutbox outbox)
    {
        _outbox = outbox;
    }

    public Task EmitMasteryUpdatedAsync(
        Guid tenantId,
        Guid studentId,
        string curriculumType,
        Guid subjectId,
        Guid? topicId,
        decimal priorScore,
        decimal newScore,
        string priorBand,
        string newBand,
        string calculationVersion,
        string correlationId,
        CancellationToken ct = default)
    {
        var scope = new
        {
            curriculum_type = curriculumType,
            subject_id = subjectId,
            topic_id = topicId,
        };
        var payload = new
        {
            subject_id = subjectId,
            topic_id = topicId,
            prior_score = priorScore,
            new_score = newScore,
            prior_band = priorBand,
            new_band = newBand,
            calculation_version = calculationVersion,
        };
        return _outbox.EnqueueAsync(
            Phase4DownstreamEventKind.mastery_updated,
            tenantId,
            studentId,
            scope,
            payload,
            correlationId,
            occurredAt: null,
            ct);
    }

    public Task EmitStreakChangedAsync(
        Guid tenantId,
        Guid studentId,
        int priorLength,
        int newLength,
        string familyTimezone,
        string correlationId,
        CancellationToken ct = default)
    {
        var scope = new { };
        var payload = new
        {
            prior_length = priorLength,
            new_length = newLength,
            @event = newLength > priorLength ? "incremented" : "reset",
            family_timezone = familyTimezone,
        };
        return _outbox.EnqueueAsync(
            Phase4DownstreamEventKind.streak_changed,
            tenantId,
            studentId,
            scope,
            payload,
            correlationId,
            occurredAt: null,
            ct);
    }

    public Task EmitBadgeAwardedAsync(
        Guid tenantId,
        Guid studentId,
        BadgeAward award,
        string badgeKey,
        string correlationId,
        CancellationToken ct = default)
    {
        var scope = new { };
        var payload = new
        {
            badge_award_id = award.BadgeAwardId,
            badge_key = badgeKey,
            badge_criterion_version = award.BadgeCriterionVersion,
            awarded_at = award.AwardedAt,
        };
        return _outbox.EnqueueAsync(
            Phase4DownstreamEventKind.badge_awarded,
            tenantId,
            studentId,
            scope,
            payload,
            correlationId,
            occurredAt: award.AwardedAt,
            ct);
    }
}

public static class Phase4DownstreamEventEmitterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4DownstreamEventEmitter(this IServiceCollection services)
    {
        services.AddScoped<IPhase4DownstreamEventEmitter, Phase4DownstreamEventEmitter>();
        return services;
    }
}
