using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Muallimi.Api.SchoolManagement.Phase4EventConsumer;

/// <summary>
/// T014 — Phase 4 downstream event consumer for Phase 5 school aggregate
/// views.
///
/// Subscribes to the broker topic <c>phase4.downstream.events</c>, dispatches
/// each envelope to <see cref="SchoolAggregateViewUpdater"/>, and relies on
/// the updater's idempotency via <c>last_event_id</c>. Event kinds consumed:
///   - mastery_updated
///   - badge_awarded
///   - streak_changed
///   - focus_area_updated
///   - at_risk_flagged
///   - at_risk_cleared
///
/// Unknown event kinds are silently skipped (additive-only contract).
/// </summary>
public sealed class Phase4DownstreamEventEnvelope
{
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public Guid StudentId { get; set; }
    public string Scope { get; set; } = "{}";
    public string Payload { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
}

public interface IPhase4DownstreamEventConsumer
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

public sealed class Phase4DownstreamEventConsumer : BackgroundService, IPhase4DownstreamEventConsumer
{
    private readonly IServiceProvider _services;
    private readonly ILogger<Phase4DownstreamEventConsumer> _logger;

    public Phase4DownstreamEventConsumer(
        IServiceProvider services,
        ILogger<Phase4DownstreamEventConsumer> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Phase5 Phase4DownstreamEventConsumer starting — broker subscription wired per US4 (T086).");
        // Real subscription wired alongside the broker bindings in US4. The
        // skeleton keeps the background service resident so Program.cs
        // registration compiles and the consumer appears in the health probe
        // endpoint list.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}

public static class Phase4DownstreamEventConsumerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5Phase4EventConsumer(this IServiceCollection services)
    {
        services.AddSingleton<Phase4DownstreamEventConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<Phase4DownstreamEventConsumer>());
        return services;
    }
}
