using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Infrastructure.Queue;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Muallimi.Api.Engagement.ProgressIngestion;

/// <summary>
/// T011 — Phase 3 session-event broker subscription.
///
/// Subscribes to the <c>phase3.session.events.phase4</c> queue that the
/// Phase 3 dispatcher publishes to. Each delivered message is decoded into a
/// <see cref="Phase3EventEnvelope"/> and handed to
/// <see cref="IProgressIngestionWorker.ProcessAsync"/>, which materialises a
/// <c>ProgressRecord</c> row idempotently.
///
/// Acknowledgement model: ACK on successful worker completion, NACK-requeue
/// on transient failure, NACK-no-requeue on permanent failure (the dead-
/// letter path is added in T041). Correlation ID is propagated via the
/// envelope field — the shared broker property is unused so the audit trail
/// always points back to the Phase 3 session event.
/// </summary>
public sealed class Phase3EventConsumer : BackgroundService
{
    public const string QueueName = "phase3.session.events.phase4";
    public const string ExchangeName = "phase3.session.events";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _rabbit;
    private readonly ILogger<Phase3EventConsumer> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public Phase3EventConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> rabbit,
        ILogger<Phase3EventConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _rabbit = rabbit.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Phase3EventConsumer starting (queue={Queue})", QueueName);
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _rabbit.Hostname,
                Port = _rabbit.Port,
                UserName = _rabbit.Username,
                Password = _rabbit.Password,
            };
            _connection = await factory.CreateConnectionAsync(stoppingToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await _channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
            await _channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: stoppingToken);
            await _channel.QueueBindAsync(QueueName, ExchangeName, routingKey: "#", arguments: null, cancellationToken: stoppingToken);
            await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 32, global: false, cancellationToken: stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (_, ea) => await OnMessageAsync(ea, stoppingToken);
            await _channel.BasicConsumeAsync(QueueName, autoAck: false, consumer, cancellationToken: stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phase3EventConsumer failed to start");
        }
    }

    private async Task OnMessageAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var channel = _channel;
        if (channel is null) return;

        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.ToArray());
            var envelope = JsonSerializer.Deserialize<Phase3EventEnvelope>(json);
            if (envelope is null || string.IsNullOrWhiteSpace(envelope.SourceEventId))
            {
                _logger.LogWarning("Phase3EventConsumer dropped malformed payload");
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken: ct);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var worker = scope.ServiceProvider.GetRequiredService<IProgressIngestionWorker>();
            await worker.ProcessAsync(envelope, ct);
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phase3EventConsumer failed to process delivery {Tag}", ea.DeliveryTag);
            try
            {
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken: ct);
            }
            catch
            {
                // already disposed or shutting down; nothing to do
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null) await _channel.CloseAsync(cancellationToken);
        if (_connection is not null) await _connection.CloseAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}

public static class Phase3EventConsumerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4Phase3EventConsumer(this IServiceCollection services)
    {
        services.AddHostedService<Phase3EventConsumer>();
        return services;
    }
}
