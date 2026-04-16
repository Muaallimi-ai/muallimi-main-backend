using System.Collections.Concurrent;

namespace Muallimi.Infrastructure.PromptAudit;

/// <summary>
/// T078 (US4) — Transport for prompt lifecycle events that the ai-service
/// registry client consumes to invalidate its cache. In local dev and in
/// tests this is an in-memory fan-out; in production it binds to the same
/// broker used by the routing-config publisher (local queue / RabbitMQ).
/// Event names are <c>prompt.promoted</c>, <c>prompt.archived</c>, and
/// <c>prompt.rolledback</c> per the prompt-registry contract.
/// </summary>
public interface IPromptRegistryEventPublisher
{
    Task PublishAsync(PromptRegistryEvent @event, CancellationToken ct = default);
}

public record PromptRegistryEvent(
    string EventId,
    string EventType,
    Guid PromptId,
    Guid? VersionId,
    string ActorId,
    string CorrelationId,
    DateTime OccurredAt);

public static class PromptRegistryEventTypes
{
    public const string Promoted = "prompt.promoted";
    public const string Archived = "prompt.archived";
    public const string RolledBack = "prompt.rolledback";
}

public class InMemoryPromptRegistryEventPublisher : IPromptRegistryEventPublisher
{
    private readonly ConcurrentQueue<PromptRegistryEvent> _published = new();

    public IReadOnlyCollection<PromptRegistryEvent> Published => _published.ToArray();

    public Task PublishAsync(PromptRegistryEvent @event, CancellationToken ct = default)
    {
        _published.Enqueue(@event);
        return Task.CompletedTask;
    }
}
