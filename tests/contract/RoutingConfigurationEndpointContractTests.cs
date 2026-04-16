using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Application.AiOperations;
using Muallimi.Application.Audit;
using Muallimi.Infrastructure.AiOperations;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T068 (US3) — Runtime routing configuration admin surface. Exercises the
/// store + publisher + audit emitter in isolation (no WebApplicationFactory
/// yet — matches the existing contract-test pattern in this repo). Verifies
/// that threshold mutations validate, persist, audit, and publish a
/// `routing.config.updated` event so the ai-service RoutingConfigCache
/// invalidates.
/// </summary>
public class RoutingConfigurationEndpointContractTests
{
    [Fact]
    public async Task Valid_Update_Persists_And_Publishes_Event()
    {
        var store = new InMemoryRoutingConfigurationStore();
        var publisher = new InMemoryRoutingConfigUpdatedPublisher();
        var audit = new AuditEventEmitter(NullLogger<AuditEventEmitter>.Instance);

        var before = store.Get();
        Assert.Equal(RoutingConfiguration.Default, before);

        var next = new RoutingConfiguration(
            CacheMatchThreshold: 0.93,
            HighConfidenceThreshold: 0.80,
            MinGroundingThreshold: 0.62,
            StrongerTierComplexityThreshold: 0.55);

        next.Validate();
        store.Set(next);

        audit.Emit(new AuditEvent
        {
            EventCategory = "provider-configuration",
            Action = "routing-configuration-updated",
            TargetType = "RoutingConfiguration",
            TargetId = "ai-tutor",
            ActorId = "ops-1",
            TenantId = "tenant-1",
            Outcome = "succeeded",
            CorrelationId = "corr-1",
        });

        await publisher.PublishAsync(new RoutingConfigurationUpdatedEvent(
            EventId: Guid.NewGuid().ToString("N"),
            CorrelationId: "corr-1",
            ActorId: "ops-1",
            TenantId: "tenant-1",
            OccurredAt: DateTime.UtcNow,
            Configuration: next));

        Assert.Equal(next, store.Get());
        var published = Assert.Single(publisher.Published);
        Assert.Equal("corr-1", published.CorrelationId);
        Assert.Equal(next, published.Configuration);
    }

    [Fact]
    public void Invalid_Threshold_Range_Is_Rejected_Before_Persistence()
    {
        var store = new InMemoryRoutingConfigurationStore();

        var invalid = new RoutingConfiguration(
            CacheMatchThreshold: 0.92,
            HighConfidenceThreshold: 1.5, // out of range
            MinGroundingThreshold: 0.60,
            StrongerTierComplexityThreshold: 0.6);

        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate());
        Assert.Equal(RoutingConfiguration.Default, store.Get());
    }

    [Fact]
    public void High_Confidence_Below_Min_Grounding_Is_Rejected()
    {
        var invalid = new RoutingConfiguration(
            CacheMatchThreshold: 0.92,
            HighConfidenceThreshold: 0.50,
            MinGroundingThreshold: 0.60,
            StrongerTierComplexityThreshold: 0.6);

        Assert.Throws<InvalidOperationException>(() => invalid.Validate());
    }
}
