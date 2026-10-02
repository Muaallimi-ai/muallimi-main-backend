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
using Muallimi.Infrastructure.Persistence;
using Muallimi.Infrastructure.Queue;
using RabbitMQ.Client;

namespace Muallimi.Api.Curriculum.DownstreamEvents;

public sealed class Phase1DownstreamEventDispatcher : BackgroundService
{
    public const string ExchangeName = "phase1.downstream.events";
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _rabbit;
    private readonly ILogger<Phase1DownstreamEventDispatcher> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _exchangeDeclared;

    public Phase1DownstreamEventDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> rabbit,
        ILogger<Phase1DownstreamEventDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbit = rabbit.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Phase1DownstreamEventDispatcher started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Phase1DownstreamEventDispatcher drain iteration failed");
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

        var pending = await db.Phase1DownstreamEvents
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
                    phase1_downstream_event_id = row.Phase1DownstreamEventId,
                    event_kind = row.EventKind,
                    payload = JsonDocument.Parse(row.Payload).RootElement,
                    correlation_id = row.CorrelationId,
                    occurred_at = row.OccurredAt,
                }));
                var props = new BasicProperties
                {
                    ContentType = "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    MessageId = row.Phase1DownstreamEventId.ToString(),
                    CorrelationId = row.CorrelationId,
                    Headers = new Dictionary<string, object?>
                    {
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
                _logger.LogWarning(ex, "Phase1DownstreamEventDispatcher publish failed for id={Id} attempt={Attempt}",
                    row.Phase1DownstreamEventId, row.DispatchAttempts);
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

public static class Phase1DownstreamEventDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddPhase1DownstreamEventDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<Phase1DownstreamEventDispatcher>();
        return services;
    }
}
