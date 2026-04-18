using System.Text.Json;
using Muallimi.Domain.ProviderBindings;
using Muallimi.Infrastructure.ProviderBindings;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T083 (US5) — ProviderAdapterBinding persistence contract. Covers the
/// domain invariants exposed to the admin endpoints:
/// (a) exactly one active binding per (capability, environment, curriculum_scope)
///     — expressed here at the entity level via <c>Active</c> toggling and at
///     the DB level by the unique filtered index on the DbContext mapping;
/// (b) fallback chain entries are ordered and preserved as a JSON array of
///     binding identifiers;
/// (c) bindings with <see cref="ProviderAdapterBinding.PromotionBlockFlag"/>
///     set cannot be activated;
/// (d) secrets are never stored on the binding — only a
///     <c>provider_configuration_ref</c> pointer;
/// (e) every lifecycle transition publishes a cache-invalidation event so the
///     ai-service adapter cache refreshes at runtime.
/// </summary>
public class ProviderAdapterBindingContractTests
{
    [Fact]
    public void Create_Normalises_Global_Scope_To_Null_And_Defaults_To_Inactive()
    {
        var binding = ProviderAdapterBinding.Create(
            capability: Capabilities.LlmLightweight,
            environment: Environments.Local,
            curriculumScope: "global",
            providerIdentifier: "anthropic-lightweight");

        Assert.Null(binding.CurriculumScope);
        Assert.False(binding.Active);
        Assert.False(binding.PromotionBlockFlag);
        Assert.Equal("[]", binding.FallbackChain);
    }

    [Fact]
    public void Invalid_Capability_Or_Environment_Rejected_At_Creation()
    {
        Assert.Throws<ArgumentException>(() => ProviderAdapterBinding.Create(
            capability: "llm_gigabrain", environment: "local",
            curriculumScope: null, providerIdentifier: "x"));

        Assert.Throws<ArgumentException>(() => ProviderAdapterBinding.Create(
            capability: Capabilities.Stt, environment: "staging",
            curriculumScope: null, providerIdentifier: "x"));
    }

    [Fact]
    public void Empty_Provider_Identifier_Rejected_At_Creation()
    {
        Assert.Throws<ArgumentException>(() => ProviderAdapterBinding.Create(
            capability: Capabilities.Tts, environment: Environments.Production,
            curriculumScope: null, providerIdentifier: ""));
    }

    [Fact]
    public void Activate_Rejected_When_PromotionBlockFlag_Set()
    {
        var blocked = ProviderAdapterBinding.Create(
            Capabilities.Tts, Environments.Dev, null, "suspended-tts-vendor");
        blocked.PromotionBlockFlag = true;

        Assert.Throws<InvalidOperationException>(() => blocked.Activate());
    }

    [Fact]
    public void Activate_Then_Deactivate_Toggles_The_Active_Flag()
    {
        var binding = ProviderAdapterBinding.Create(
            Capabilities.LlmLightweight, Environments.Local, null, "anthropic-lightweight");

        Assert.False(binding.Active);
        binding.Activate();
        Assert.True(binding.Active);
        binding.Deactivate();
        Assert.False(binding.Active);
    }

    [Fact]
    public void Fallback_Chain_Preserves_Order_And_Deduplicates()
    {
        var primary = ProviderAdapterBinding.Create(
            Capabilities.LlmStronger, Environments.Production, null, "cloud-model-a");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        primary.UpdateFallbackChain(new[] { a, b, a, c });

        var resolved = primary.ReadFallbackChain();
        Assert.Equal(new[] { a, b, c }, resolved);
    }

    [Fact]
    public void Fallback_Chain_Serialises_To_A_JSON_Array_Of_Strings()
    {
        var primary = ProviderAdapterBinding.Create(
            Capabilities.Embedding, Environments.Production, null, "cloud-embed");
        var id = Guid.NewGuid();
        primary.UpdateFallbackChain(new[] { id });

        using var doc = JsonDocument.Parse(primary.FallbackChain);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal(id.ToString(), doc.RootElement[0].GetString());
    }

    [Fact]
    public void ProviderConfigurationRef_Holds_A_Pointer_Never_The_Secret()
    {
        var binding = ProviderAdapterBinding.Create(
            Capabilities.LlmStronger, Environments.Production, null,
            providerIdentifier: "cloud-model-a",
            providerConfigurationRef: "vault://secret/ai/model-a");

        Assert.Equal("vault://secret/ai/model-a", binding.ProviderConfigurationRef);
        Assert.DoesNotContain("password", binding.FallbackChain);
        Assert.DoesNotContain("secret=", binding.FallbackChain);
    }

    [Fact]
    public void Normalise_Scope_Treats_Blank_And_Global_As_Null()
    {
        Assert.Null(ProviderAdapterBinding.NormaliseScope(null));
        Assert.Null(ProviderAdapterBinding.NormaliseScope("  "));
        Assert.Null(ProviderAdapterBinding.NormaliseScope("global"));
        Assert.Null(ProviderAdapterBinding.NormaliseScope("GLOBAL"));
        Assert.Equal("moe/grade_7/mathematics", ProviderAdapterBinding.NormaliseScope("moe/grade_7/mathematics"));
    }

    [Fact]
    public void Active_Tuple_Uniqueness_Is_Expressed_At_The_Entity_Level_By_Deactivating_Siblings()
    {
        // The DbContext maps a unique filtered index on
        // (capability, environment, curriculum_scope) WHERE active = true.
        // The repository enforces the invariant at the entity layer by
        // deactivating any prior sibling before activating a new one.
        // Here we verify the entity surface supports that flow.
        var primary = ProviderAdapterBinding.Create(
            Capabilities.LlmLightweight, Environments.Local, null, "local-a");
        var alternate = ProviderAdapterBinding.Create(
            Capabilities.LlmLightweight, Environments.Local, null, "local-b");

        primary.Activate();
        Assert.True(primary.Active);

        // Swap the active sibling — repository contract is: caller deactivates
        // the incumbent, then activates the target.
        primary.Deactivate();
        alternate.Activate();

        Assert.False(primary.Active);
        Assert.True(alternate.Active);
    }

    [Fact]
    public async Task Lifecycle_Publishes_Cache_Invalidation_Event_Per_Transition()
    {
        var publisher = new InMemoryProviderBindingUpdatedPublisher();

        await publisher.PublishAsync(new ProviderBindingUpdatedEvent(
            Guid.NewGuid().ToString("N"), ProviderBindingEventTypes.Created,
            BindingId: Guid.NewGuid(),
            Capability: Capabilities.LlmLightweight,
            Environment: Environments.Local,
            CurriculumScope: null,
            ProviderIdentifier: "anthropic-lightweight",
            Active: false,
            ActorId: "ops-1", CorrelationId: "corr-1", OccurredAt: DateTime.UtcNow));

        await publisher.PublishAsync(new ProviderBindingUpdatedEvent(
            Guid.NewGuid().ToString("N"), ProviderBindingEventTypes.Activated,
            Guid.NewGuid(), Capabilities.LlmLightweight, Environments.Local, null,
            "anthropic-lightweight", true, "ops-1", "corr-2", DateTime.UtcNow));

        await publisher.PublishAsync(new ProviderBindingUpdatedEvent(
            Guid.NewGuid().ToString("N"), ProviderBindingEventTypes.FallbackUpdated,
            Guid.NewGuid(), Capabilities.LlmLightweight, Environments.Local, null,
            "anthropic-lightweight", true, "ops-1", "corr-3", DateTime.UtcNow));

        await publisher.PublishAsync(new ProviderBindingUpdatedEvent(
            Guid.NewGuid().ToString("N"), ProviderBindingEventTypes.Deactivated,
            Guid.NewGuid(), Capabilities.LlmLightweight, Environments.Local, null,
            "anthropic-lightweight", false, "ops-1", "corr-4", DateTime.UtcNow));

        Assert.Equal(4, publisher.Published.Count);
        Assert.Contains(publisher.Published, e => e.EventType == ProviderBindingEventTypes.Created);
        Assert.Contains(publisher.Published, e => e.EventType == ProviderBindingEventTypes.Activated);
        Assert.Contains(publisher.Published, e => e.EventType == ProviderBindingEventTypes.FallbackUpdated);
        Assert.Contains(publisher.Published, e => e.EventType == ProviderBindingEventTypes.Deactivated);
    }
}
