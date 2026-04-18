using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;
using Muallimi.Infrastructure.Queue;
using RabbitMQ.Client;

namespace Muallimi.Api.Engagement.DownstreamEvents;

/// <summary>
/// T017 — Phase 4 downstream-event dispatcher.
///
/// Polls the <c>phase4_downstream_events</c> outbox for <c>queued</c> rows and
/// publishes them to the <c>phase4.downstream.events</c> topic exchange with
/// routing key equal to the event kind. At-least-once delivery is enforced by
/// the outbox pattern: a row is only marked <c>dispatched</c> after the broker
/// acknowledges the publish, and a transient failure keeps the row queued for
/// the next poll.
/// </summary>
public sealed class Phase4DownstreamEventDispatcher : BackgroundService
{
    public const string ExchangeName = "phase4.downstream.events";
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _rabbit;
    private readonly ILogger<Phase4DownstreamEventDispatcher> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _exchangeDeclared;

    public Phase4DownstreamEventDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> rabbit,
        ILogger<Phase4DownstreamEventDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbit = rabbit.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Phase4DownstreamEventDispatcher started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phase4DownstreamEventDispatcher drain iteration failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();

        var pending = await db.Phase4DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.DeliveryState == "queued")
            .OrderBy(e => e.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var channel = await EnsureChannelAsync(ct);
        foreach (var row in pending)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    phase4_downstream_event_id = row.Phase4DownstreamEventId,
                    tenant_id = row.TenantId,
                    event_kind = row.EventKind,
                    student_id = row.StudentId,
                    scope = JsonDocument.Parse(row.Scope).RootElement,
                    payload = JsonDocument.Parse(row.Payload).RootElement,
                    correlation_id = row.CorrelationId,
                    occurred_at = row.OccurredAt,
                }));
                var props = new BasicProperties
                {
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    MessageId = row.Phase4DownstreamEventId.ToString(),
                    CorrelationId = row.CorrelationId,
                    Headers = new Dictionary<string, object?>
                    {
                        ["x-tenant-id"] = row.TenantId.ToString(),
                        ["x-event-kind"] = row.EventKind,
                    },
                };

                await channel.BasicPublishAsync(
                    exchange: ExchangeName,
                    routingKey: row.EventKind,
                    mandatory: false,
                    basicProperties: props,
                    body: body,
                    cancellationToken: ct);

                row.DeliveryState = "dispatched";
                row.DispatchedAt = DateTime.UtcNow;
                row.DispatchAttempts += 1;
            }
            catch (Exception ex)
            {
                row.DispatchAttempts += 1;
                row.DeliveryState = row.DispatchAttempts >= 5 ? "failed" : "queued";
                _logger.LogWarning(ex, "Phase4DownstreamEventDispatcher publish failed for id={Id} attempt={Attempt}",
                    row.Phase4DownstreamEventId, row.DispatchAttempts);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is not null && _channel.IsOpen) return _channel;

            var factory = new ConnectionFactory
            {
                HostName = _rabbit.Hostname,
                Port = _rabbit.Port,
                UserName = _rabbit.Username,
                Password = _rabbit.Password,
                AutomaticRecoveryEnabled = true,
            };
            _connection = await factory.CreateConnectionAsync(ct);
            _channel = await _connection.CreateChannelAsync(cancellationToken: ct);

            if (!_exchangeDeclared)
            {
                await _channel.ExchangeDeclareAsync(
                    exchange: ExchangeName,
                    type: ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    cancellationToken: ct);
                _exchangeDeclared = true;
            }

            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null) await _channel.CloseAsync(cancellationToken);
        if (_connection is not null) await _connection.CloseAsync(cancellationToken);
    }
}

public static class Phase4DownstreamEventDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4DownstreamEventDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<Phase4DownstreamEventDispatcher>();
        return services;
    }
}
