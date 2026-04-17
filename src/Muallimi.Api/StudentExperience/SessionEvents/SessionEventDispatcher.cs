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
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;
using Muallimi.Infrastructure.Queue;
using RabbitMQ.Client;

namespace Muallimi.Api.StudentExperience.SessionEvents;

/// <summary>
/// T014 — SessionEventDispatcher. A <see cref="BackgroundService"/> that
/// polls the <c>session_events</c> outbox for <c>pending</c> rows and
/// publishes them to the local queue/broker
/// (<c>student.session.events</c> topic exchange per the Phase 3 compose
/// overlay). At-least-once delivery is enforced by the transactional outbox
/// pattern: the dispatcher only marks a row <c>published</c> after the
/// broker returns success, and a failed publish keeps the row pending for
/// the next poll.
///
/// Observability: every publish carries the <c>X-Correlation-Id</c> of the
/// originating student request so Phase 4 consumers can cross-reference.
///
/// Not a runtime surface for students — only the dispatcher path lives here.
/// </summary>
public sealed class SessionEventDispatcher : BackgroundService
{
    private const string ExchangeName = "student.session.events";
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _rabbit;
    private readonly ILogger<SessionEventDispatcher> _logger;

    private IConnection? _connection;
    private IChannel? _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _exchangeDeclared;

    public SessionEventDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> rabbit,
        ILogger<SessionEventDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbit = rabbit.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionEventDispatcher started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SessionEventDispatcher drain iteration failed");
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

        // IgnoreQueryFilters because the dispatcher is a background service
        // that serves all tenants; the payload itself carries tenant_id so
        // the consumer can re-scope.
        var batch = await db.SessionEvents
            .IgnoreQueryFilters()
            .Where(e => e.DispatchState == "pending")
            .OrderBy(e => e.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return;

        var channel = await EnsureChannelAsync(ct);
        foreach (var row in batch)
        {
            var ok = await TryPublishAsync(channel, row, ct);
            row.DispatchAttempts += 1;
            if (ok)
            {
                row.DispatchState = "published";
                row.DispatchedAt = DateTime.UtcNow;
            }
            else if (row.DispatchAttempts >= 10)
            {
                row.DispatchState = "failed";
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Dispatched {Count} session events", batch.Count(r => r.DispatchState == "published"));
    }

    private async Task<bool> TryPublishAsync(IChannel channel, SessionEvent row, CancellationToken ct)
    {
        try
        {
            var envelope = new
            {
                id = row.Id,
                tenant_id = row.TenantId,
                student_session_id = row.StudentSessionId,
                correlation_id = row.CorrelationId,
                event_kind = row.EventKind,
                event_payload = JsonDocument.Parse(row.EventPayload).RootElement,
                curriculum_scope = JsonDocument.Parse(row.CurriculumScope).RootElement,
                plan_tier_snapshot = row.PlanTierSnapshot,
                created_at = row.CreatedAt,
            };

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
            var props = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = row.Id.ToString(),
                CorrelationId = row.CorrelationId.ToString(),
                Headers = new Dictionary<string, object?>
                {
                    ["x-tenant-id"] = row.TenantId.ToString(),
                    ["x-event-kind"] = row.EventKind,
                },
            };

            await channel.BasicPublishAsync(
                exchange: ExchangeName,
                routingKey: $"phase4.{row.EventKind}",
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish session event {EventId}", row.Id);
            return false;
        }
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

public static class SessionEventDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3SessionEventDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<SessionEventDispatcher>();
        return services;
    }
}
