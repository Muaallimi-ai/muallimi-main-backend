namespace Muallimi.Application.AiOperations;

/// <summary>
/// T068 (US3) — Runtime-configurable routing thresholds mirrored in the
/// main-backend admin surface. These values govern the AI tutor's routing
/// coordinator in ai-service; every mutation is audited and fans out via
/// <see cref="IRoutingConfigUpdatedPublisher"/> so the ai-service
/// RoutingConfigCache invalidates without a redeploy (SC-009).
/// </summary>
public record RoutingConfiguration(
    double CacheMatchThreshold,
    double HighConfidenceThreshold,
    double MinGroundingThreshold,
    double StrongerTierComplexityThreshold)
{
    public static RoutingConfiguration Default => new(
        CacheMatchThreshold: 0.92,
        HighConfidenceThreshold: 0.78,
        MinGroundingThreshold: 0.60,
        StrongerTierComplexityThreshold: 0.6);

    public void Validate()
    {
        EnsureInRange(CacheMatchThreshold, nameof(CacheMatchThreshold));
        EnsureInRange(HighConfidenceThreshold, nameof(HighConfidenceThreshold));
        EnsureInRange(MinGroundingThreshold, nameof(MinGroundingThreshold));
        EnsureInRange(StrongerTierComplexityThreshold, nameof(StrongerTierComplexityThreshold));
        if (HighConfidenceThreshold <= MinGroundingThreshold)
            throw new InvalidOperationException(
                "high_confidence_threshold must exceed min_grounding_threshold.");
    }

    private static void EnsureInRange(double value, string name)
    {
        if (value is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(name, "must be in (0, 1].");
    }
}

/// <summary>
/// Source of truth for the active routing configuration. Implementations
/// are expected to be atomic so concurrent updates do not interleave.
/// </summary>
public interface IRoutingConfigurationStore
{
    RoutingConfiguration Get();
    void Set(RoutingConfiguration configuration);
}

/// <summary>
/// Publishes `routing.config.updated` events onto the local broker so
/// ai-service RoutingConfigCache instances invalidate. Keyed by the Phase 2
/// foundational topic defined in T006.
/// </summary>
public interface IRoutingConfigUpdatedPublisher
{
    Task PublishAsync(RoutingConfigurationUpdatedEvent evt, CancellationToken ct = default);
}

public record RoutingConfigurationUpdatedEvent(
    string EventId,
    string CorrelationId,
    string ActorId,
    string TenantId,
    DateTime OccurredAt,
    RoutingConfiguration Configuration);
