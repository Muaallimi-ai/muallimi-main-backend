using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.DownstreamEvents;

/// <summary>
/// T016 — Phase 5 downstream-event outbox writer.
///
/// Callers enqueue a <see cref="Phase5DownstreamEvent"/> inside the same
/// <c>MuallimiDbContext</c> unit of work that wrote the originating state
/// change (school creation, roster import completion, exam publish, license
/// update, announcement send). The dispatcher
/// (<see cref="Phase5DownstreamEventDispatcher"/>) drains rows to the broker
/// with at-least-once delivery.
///
/// Additive-only invariant: every new kind is a minor bump; consumers MUST
/// ignore unknown kinds.
/// </summary>
public enum Phase5DownstreamEventKind
{
    school_created,
    roster_imported,
    exam_published,
    license_updated,
    announcement_sent,
    report_generated,
}

public interface IPhase5DownstreamEventOutbox
{
    Task<Guid> EnqueueAsync(
        Phase5DownstreamEventKind kind,
        Guid tenantId,
        Guid schoolTenantId,
        object payload,
        string correlationId,
        DateTime? occurredAt = null,
        CancellationToken ct = default);
}

public sealed class Phase5DownstreamEventOutbox : IPhase5DownstreamEventOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public Phase5DownstreamEventOutbox(MuallimiDbContext db)
    {
        _db = db;
    }

    public Task<Guid> EnqueueAsync(
        Phase5DownstreamEventKind kind,
        Guid tenantId,
        Guid schoolTenantId,
        object payload,
        string correlationId,
        DateTime? occurredAt = null,
        CancellationToken ct = default)
    {
        var row = new Phase5DownstreamEvent
        {
            Phase5EventId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            EventKind = kind.ToString(),
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            CorrelationId = correlationId,
            OccurredAt = (occurredAt ?? DateTime.UtcNow).ToUniversalTime(),
            SchemaVersion = "1.0.0",
            DeliveryState = "queued",
            DispatchAttempts = 0,
        };
        _db.Phase5DownstreamEvents.Add(row);
        return Task.FromResult(row.Phase5EventId);
    }
}

public static class Phase5DownstreamEventOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5DownstreamEventOutbox(this IServiceCollection services)
    {
        services.AddScoped<IPhase5DownstreamEventOutbox, Phase5DownstreamEventOutbox>();
        return services;
    }
}
