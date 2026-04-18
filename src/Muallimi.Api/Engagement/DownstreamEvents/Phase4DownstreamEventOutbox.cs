using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.DownstreamEvents;

/// <summary>
/// T017 — Phase 4 downstream-event outbox writer.
///
/// Callers enqueue a <see cref="Phase4DownstreamEvent"/> inside the same
/// <c>MuallimiDbContext</c> unit of work that wrote the originating state
/// change (mastery update, badge award, streak change, focus-area update,
/// weekly report generation, at-risk flag raise/clear). The dispatcher
/// (see <see cref="Phase4DownstreamEventDispatcher"/>) drains rows to the
/// local broker with at-least-once delivery.
///
/// Additive-only invariant: every new kind is a minor bump; consumers MUST
/// ignore unknown kinds.
/// </summary>
public enum Phase4DownstreamEventKind
{
    mastery_updated,
    badge_awarded,
    streak_changed,
    focus_area_updated,
    weekly_report_generated,
    at_risk_flagged,
    at_risk_cleared,
}

public interface IPhase4DownstreamEventOutbox
{
    Task<Guid> EnqueueAsync(
        Phase4DownstreamEventKind kind,
        Guid tenantId,
        Guid studentId,
        object scope,
        object payload,
        string correlationId,
        DateTime? occurredAt = null,
        CancellationToken ct = default);
}

public sealed class Phase4DownstreamEventOutbox : IPhase4DownstreamEventOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public Phase4DownstreamEventOutbox(MuallimiDbContext db)
    {
        _db = db;
    }

    public Task<Guid> EnqueueAsync(
        Phase4DownstreamEventKind kind,
        Guid tenantId,
        Guid studentId,
        object scope,
        object payload,
        string correlationId,
        DateTime? occurredAt = null,
        CancellationToken ct = default)
    {
        var row = new Phase4DownstreamEvent
        {
            Phase4DownstreamEventId = Guid.NewGuid(),
            TenantId = tenantId,
            EventKind = kind.ToString(),
            StudentId = studentId,
            Scope = JsonSerializer.Serialize(scope, JsonOptions),
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            CorrelationId = correlationId,
            OccurredAt = (occurredAt ?? DateTime.UtcNow).ToUniversalTime(),
            DeliveryState = "queued",
            DispatchAttempts = 0,
        };
        _db.Phase4DownstreamEvents.Add(row);
        return Task.FromResult(row.Phase4DownstreamEventId);
    }
}

public static class Phase4DownstreamEventOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4DownstreamEventOutbox(this IServiceCollection services)
    {
        services.AddScoped<IPhase4DownstreamEventOutbox, Phase4DownstreamEventOutbox>();
        return services;
    }
}
