using System.Collections.Concurrent;

namespace Muallimi.Infrastructure.ProviderBindings;

/// <summary>
/// T090 (US5) — Transport for <c>provider.binding.updated</c> events. The
/// ai-service provider-binding cache invalidates on every event so the next
/// request observes the new primary / fallback chain without a redeploy
/// (provider-adapter-contract invariant: runtime binding changes take effect
/// without code deploy). Local dev uses the in-memory fan-out; production
/// wires the same broker used by the routing-config and prompt-registry
/// publishers.
/// </summary>
public interface IProviderBindingUpdatedPublisher
{
    Task PublishAsync(ProviderBindingUpdatedEvent @event, CancellationToken ct = default);
}

public record ProviderBindingUpdatedEvent(
    string EventId,
    string EventType,
    Guid BindingId,
    string Capability,
    string Environment,
    string? CurriculumScope,
    string ProviderIdentifier,
    bool Active,
    string ActorId,
    string CorrelationId,
    DateTime OccurredAt);

public static class ProviderBindingEventTypes
{
    public const string Created = "provider.binding.created";
    public const string Activated = "provider.binding.activated";
    public const string Deactivated = "provider.binding.deactivated";
    public const string FallbackUpdated = "provider.binding.fallback_updated";
}

public class InMemoryProviderBindingUpdatedPublisher : IProviderBindingUpdatedPublisher
{
    private readonly ConcurrentQueue<ProviderBindingUpdatedEvent> _published = new();

    public IReadOnlyCollection<ProviderBindingUpdatedEvent> Published => _published.ToArray();

    public Task PublishAsync(ProviderBindingUpdatedEvent @event, CancellationToken ct = default)
    {
        _published.Enqueue(@event);
        return Task.CompletedTask;
    }
}
