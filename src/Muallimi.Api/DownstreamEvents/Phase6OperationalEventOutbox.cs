using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.DownstreamEvents;

/// <summary>
/// T013 — Phase 6 operational event outbox. Services call <see cref="EnqueueAsync"/>
/// to append a Phase6OperationalEvent row in the same DB transaction that makes
/// the state change. The background dispatcher drains undispatched rows to the
/// local queue/broker on topic <c>phase6.operational.events</c>.
///
/// Event kinds: subscription_created, payment_processed, payment_failed,
/// alert_fired, incident_opened, incident_resolved, data_deletion_completed.
/// </summary>
public class Phase6OperationalEventOutbox
{
    public const string ExchangeName = "phase6.operational.events";

    private readonly MuallimiDbContext _db;

    public Phase6OperationalEventOutbox(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task EnqueueAsync(
        Guid tenantId,
        string eventKind,
        object payload,
        string correlationId,
        CancellationToken ct = default)
    {
        _db.Phase6OperationalEvents.Add(new Phase6OperationalEvent
        {
            EventId = Guid.NewGuid(),
            TenantId = tenantId,
            EventKind = eventKind,
            Payload = payload is string s ? s : JsonSerializer.Serialize(payload),
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow,
            SchemaVersion = "1.0.0"
        });
        await _db.SaveChangesAsync(ct);
    }
}

public class Phase6OperationalEventDispatcher : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<Phase6OperationalEventDispatcher> _logger;

    public Phase6OperationalEventDispatcher(
        IServiceProvider serviceProvider,
        ILogger<Phase6OperationalEventDispatcher> logger)
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

                var pending = await db.Phase6OperationalEvents
                    .Where(e => e.DispatchedAt == null)
                    .OrderBy(e => e.OccurredAt)
                    .Take(100)
                    .ToListAsync(stoppingToken);

                foreach (var evt in pending)
                {
                    // Local parity: a broker adapter will be wired in US work.
                    // For now, mark dispatched after logging the payload.
                    _logger.LogInformation(
                        "phase6.operational.events: {EventKind} tenant={TenantId} correlationId={CorrelationId}",
                        evt.EventKind, evt.TenantId, evt.CorrelationId);
                    evt.DispatchedAt = DateTime.UtcNow;
                }

                if (pending.Count > 0) await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phase6OperationalEventDispatcher drain cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
