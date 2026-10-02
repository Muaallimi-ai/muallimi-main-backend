using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Muallimi.Infrastructure.Queue;

/// <summary>
/// Publishes per-node content-fetch jobs to a dedicated exchange the
/// ingestion worker consumes from. Separate from the structure-extraction
/// queue (<c>curriculum.ingestion</c>) so the two streams have independent
/// backpressure: a slow content fetch doesn't stall structure jobs and a
/// burst of structure uploads doesn't push a content click to the back.
///
/// Single long-lived connection + channel reused across publishes; calls
/// serialised through a semaphore because IChannel.PublishAsync is not
/// thread-safe.
/// </summary>
public sealed class RabbitMqNodeContentJobPublisher : INodeContentJobPublisher, IAsyncDisposable
{
    private const string ExchangeName = "curriculum.node-content";
    private const string RoutingKey = "curriculum.node-content.fetch";

    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqNodeContentJobPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _channel;
    private bool _exchangeDeclared;

    public RabbitMqNodeContentJobPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqNodeContentJobPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync(NodeContentMessage message, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var channel = await EnsureChannelAsync(ct);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

            var props = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = message.NodeId.ToString(),
                CorrelationId = message.CorrelationId,
                Headers = new Dictionary<string, object?>
                {
                    ["x-source-id"] = message.SourceId.ToString(),
                    ["x-node-id"] = message.NodeId.ToString(),
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
                "Published node-content fetch for node {NodeId} (source {SourceId}, {PageCount} pages) to {Exchange}/{RoutingKey}",
                message.NodeId, message.SourceId, message.PageRefs.Length, ExchangeName, RoutingKey);
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

        _logger.LogInformation("RabbitMqNodeContentJobPublisher connected to RabbitMQ at {Host}:{Port}", _options.Hostname, _options.Port);
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
