using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Muallimi.Infrastructure.Queue;

/// <summary>
/// Publishes ingestion jobs to the <c>curriculum.ingestion</c> topic exchange
/// that the document-ingestion worker consumes from
/// (queue <c>curriculum.ingestion-jobs</c>, routing pattern <c>curriculum.ingestion.#</c>).
///
/// Opens a single long-lived connection + channel on first publish and reuses them.
/// Safe for concurrent publishes; RabbitMQ.Client IChannel is not thread-safe for
/// publish, so calls are serialised through a semaphore.
/// </summary>
public sealed class RabbitMqIngestionJobPublisher : IIngestionJobPublisher, IAsyncDisposable
{
    private const string ExchangeName = "curriculum.ingestion";
    private const string RoutingKey = "curriculum.ingestion.upload";

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqIngestionJobPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;
    private bool _exchangeDeclared;

    public RabbitMqIngestionJobPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqIngestionJobPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync(IngestionMessage message, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var channel = await EnsureChannelAsync(ct);

            var body = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(message));

            var props = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = message.JobId.ToString(),
                CorrelationId = message.CorrelationId,
                Headers = new Dictionary<string, object?>
                {
                    ["x-source-id"] = message.SourceId.ToString(),
                    ["x-curriculum-type"] = message.CurriculumType,
                }
            };

            await channel.BasicPublishAsync(
                exchange: ExchangeName,
                routingKey: RoutingKey,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: ct);

            _logger.LogInformation(
                "Published ingestion job {JobId} (source {SourceId}) to {Exchange}/{RoutingKey}",
                message.JobId, message.SourceId, ExchangeName, RoutingKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is not null && _channel.IsOpen)
            return _channel;

        var factory = new ConnectionFactory
        {
            HostName = _options.Hostname,
            Port = _options.Port,
            UserName = _options.Username,
            Password = _options.Password,
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

        _logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}", _options.Hostname, _options.Port);
        return _channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
            await _channel.CloseAsync();
        if (_connection is not null)
            await _connection.CloseAsync();
    }
}

public sealed class RabbitMqOptions
{
    public string Hostname { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "muallimi";
    public string Password { get; set; } = "muallimi_local";
}
