using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T148 (US8) — Emits <c>at_risk_flagged</c> and <c>at_risk_cleared</c>
/// downstream events through the shared
/// <see cref="IPhase4DownstreamEventOutbox"/> inside the same unit of work
/// that wrote the originating <see cref="AtRiskFlag"/>. Phase 5 consumers
/// receive the threshold version + correlation id so they can correlate
/// upstream signals with downstream nudges.
/// </summary>
public interface IAtRiskEventEmitter
{
    Task EmitFlaggedAsync(AtRiskFlag flag, Guid? interventionPromptId, CancellationToken ct = default);

    Task EmitClearedAsync(AtRiskFlag flag, CancellationToken ct = default);
}

public sealed class AtRiskEventEmitter : IAtRiskEventEmitter
{
    private readonly IPhase4DownstreamEventOutbox _outbox;

    public AtRiskEventEmitter(IPhase4DownstreamEventOutbox outbox)
    {
        _outbox = outbox;
    }

    public Task EmitFlaggedAsync(AtRiskFlag flag, Guid? interventionPromptId, CancellationToken ct = default)
    {
        var scope = new
        {
            threshold_version = flag.ThresholdVersion,
        };
        var payload = new
        {
            at_risk_flag_id = flag.AtRiskFlagId,
            raised_at = flag.RaisedAt,
            threshold_version = flag.ThresholdVersion,
            triggering_evidence = flag.TriggeringEvidence,
            intervention_prompt_id = interventionPromptId,
        };
        return _outbox.EnqueueAsync(
            Phase4DownstreamEventKind.at_risk_flagged,
            flag.TenantId,
            flag.StudentId,
            scope,
            payload,
            flag.CorrelationId,
            occurredAt: flag.RaisedAt,
            ct);
    }

    public Task EmitClearedAsync(AtRiskFlag flag, CancellationToken ct = default)
    {
        var scope = new
        {
            threshold_version = flag.ThresholdVersion,
        };
        var payload = new
        {
            at_risk_flag_id = flag.AtRiskFlagId,
            raised_at = flag.RaisedAt,
            cleared_at = flag.ClearedAt,
            threshold_version = flag.ThresholdVersion,
        };
        return _outbox.EnqueueAsync(
            Phase4DownstreamEventKind.at_risk_cleared,
            flag.TenantId,
            flag.StudentId,
            scope,
            payload,
            flag.CorrelationId,
            occurredAt: flag.ClearedAt ?? DateTime.UtcNow,
            ct);
    }
}

public static class AtRiskEventEmitterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4AtRiskEventEmitter(this IServiceCollection services)
    {
        services.AddScoped<IAtRiskEventEmitter, AtRiskEventEmitter>();
        return services;
    }
}
