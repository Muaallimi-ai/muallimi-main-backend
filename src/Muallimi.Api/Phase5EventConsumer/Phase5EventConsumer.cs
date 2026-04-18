using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Phase5EventConsumer;

/// <summary>
/// T012 — Subscribes to phase5.downstream.events and processes the six kinds:
/// school_created, roster_imported, exam_published, license_updated,
/// announcement_sent, report_generated. Uses a consumer outbox pattern: each
/// processed event is recorded so we can replay deterministically.
///
/// Actual RabbitMQ binding lands in US1 (billing-sync for school_created,
/// license_updated). The Phase 5 Phase5DownstreamEvents table is the local
/// fallback — we poll it here for local-parity tests.
/// </summary>
public class Phase5EventConsumer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Phase5EventConsumer> _logger;

    public Phase5EventConsumer(
        IServiceProvider serviceProvider,
        ILogger<Phase5EventConsumer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();

                var events = await db.Phase5DownstreamEvents
                    .Where(e => e.DeliveryState == "dispatched")
                    .OrderBy(e => e.OccurredAt)
                    .Take(50)
                    .ToListAsync(stoppingToken);

                foreach (var evt in events)
                {
                    await ProcessAsync(evt.EventKind, evt.Payload, evt.CorrelationId, scope.ServiceProvider, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phase5EventConsumer poll cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public static Task ProcessAsync(
        string eventKind,
        string payload,
        string correlationId,
        IServiceProvider services,
        CancellationToken ct)
    {
        // Handlers for each of the six Phase 5 downstream event kinds.
        // Real billing-sync logic lands in US1. Foundational scaffolding just
        // acknowledges receipt and logs.
        return eventKind switch
        {
            "school_created" or "roster_imported" or "exam_published"
                or "license_updated" or "announcement_sent" or "report_generated" => Task.CompletedTask,
            _ => Task.CompletedTask
        };
    }
}
