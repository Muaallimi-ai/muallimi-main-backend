using Muallimi.Application.AiOperations;

namespace Muallimi.Infrastructure.AiOperations;

/// <summary>
/// T068 (US3) — In-memory store for the active routing configuration. Local
/// parity default: no database row needed at readiness. Swap for an EF Core
/// backed store once the admin surface adds persistent history (post-MVP).
/// </summary>
public class InMemoryRoutingConfigurationStore : IRoutingConfigurationStore
{
    private readonly object _lock = new();
    private RoutingConfiguration _current = RoutingConfiguration.Default;

    public RoutingConfiguration Get()
    {
        lock (_lock) return _current;
    }

    public void Set(RoutingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        lock (_lock) _current = configuration;
    }
}

/// <summary>
/// Local/in-process publisher used for tests and local-parity mode. In
/// production, replace with a RabbitMQ-backed implementation publishing to
/// the `routing.config.updated` topic (Phase 2 foundational T006).
/// </summary>
public class InMemoryRoutingConfigUpdatedPublisher : IRoutingConfigUpdatedPublisher
{
    private readonly List<RoutingConfigurationUpdatedEvent> _published = new();
    private readonly object _lock = new();

    public IReadOnlyList<RoutingConfigurationUpdatedEvent> Published
    {
        get { lock (_lock) return _published.ToArray(); }
    }

    public Task PublishAsync(RoutingConfigurationUpdatedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_lock) _published.Add(evt);
        return Task.CompletedTask;
    }
}
