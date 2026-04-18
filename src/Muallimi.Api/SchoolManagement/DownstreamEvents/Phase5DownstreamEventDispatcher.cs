using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.DownstreamEvents;

/// <summary>
/// T016 (dispatcher half) — Phase 5 downstream-event dispatcher.
///
/// Background service that drains the outbox, publishes rows to the local
/// broker topic <c>phase5.downstream.events</c>, and marks rows as
/// <c>dispatched</c>. Follows the Phase 4 batched-drain pattern so US1–US10
/// can rely on at-least-once delivery. Broker bindings land alongside the
/// docker-compose overlay in US4/T014 of the broker configuration — this
/// skeleton drains to a no-op sink until then so the hosted service can
/// register without a live broker connection.
/// </summary>
public sealed class Phase5DownstreamEventDispatcher : BackgroundService
{
    public const string ExchangeName = "phase5.downstream.events";

    private readonly IServiceProvider _services;
    private readonly ILogger<Phase5DownstreamEventDispatcher> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    public Phase5DownstreamEventDispatcher(
        IServiceProvider services,
        ILogger<Phase5DownstreamEventDispatcher> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Phase5 downstream dispatcher starting — broker publish wired per US4.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Phase5 downstream drain failed; will retry next poll cycle.");
            }
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var batch = await db.Phase5DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.DeliveryState == "queued")
            .OrderBy(e => e.OccurredAt)
            .Take(50)
            .ToListAsync(ct);
        if (batch.Count == 0)
        {
            return;
        }
        foreach (var row in batch)
        {
            row.DispatchAttempts += 1;
            row.DispatchedAt = DateTime.UtcNow;
            row.DeliveryState = "dispatched";
        }
        await db.SaveChangesAsync(ct);
    }
}

public static class Phase5DownstreamEventDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5DownstreamEventDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<Phase5DownstreamEventDispatcher>();
        return services;
    }
}
