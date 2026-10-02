using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Curriculum.DownstreamEvents;

public enum Phase1DownstreamEventKind
{
    curriculum_node_approved,
}

public interface IPhase1DownstreamEventOutbox
{
    Task<Guid> EnqueueAsync(
        Phase1DownstreamEventKind kind,
        object payload,
        string correlationId,
        DateTime? occurredAt = null,
        CancellationToken ct = default);
}

public sealed class Phase1DownstreamEventOutbox : IPhase1DownstreamEventOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;

    public Phase1DownstreamEventOutbox(MuallimiDbContext db)
    {
        _db = db;
    }

    public Task<Guid> EnqueueAsync(
        Phase1DownstreamEventKind kind,
        object payload,
        string correlationId,
        DateTime? occurredAt = null,
        CancellationToken ct = default)
    {
        var row = new Phase1DownstreamEvent
        {
            Phase1DownstreamEventId = Guid.NewGuid(),
            EventKind = ToWireKind(kind),
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            CorrelationId = correlationId ?? string.Empty,
            OccurredAt = (occurredAt ?? DateTime.UtcNow).ToUniversalTime(),
            DeliveryState = "queued",
            DispatchAttempts = 0,
        };
        _db.Phase1DownstreamEvents.Add(row);
        return Task.FromResult(row.Phase1DownstreamEventId);
    }

    // Enum values use underscores because C# identifiers can't contain dots;
    // the wire kind uses a dot so consumers see `curriculum.node.approved`.
    public static string ToWireKind(Phase1DownstreamEventKind kind) => kind switch
    {
        Phase1DownstreamEventKind.curriculum_node_approved => "curriculum.node.approved",
        _ => kind.ToString(),
    };
}

public static class Phase1DownstreamEventOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddPhase1DownstreamEventOutbox(this IServiceCollection services)
    {
        services.AddScoped<IPhase1DownstreamEventOutbox, Phase1DownstreamEventOutbox>();
        return services;
    }
}
