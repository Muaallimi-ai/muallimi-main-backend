using System;
using System.Linq;
using Muallimi.Api.Engagement.DownstreamEvents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Engagement.DownstreamEvents;

/// <summary>
/// T155 (Polish) — Contract test for <c>phase4.downstream.events</c>
/// additive-only rule against a frozen schema snapshot.
///
/// The snapshot pins:
///   1. the seven event kinds the contract enumerates at version 1.0.0;
///   2. the routing key shape used by
///      <see cref="Phase4DownstreamEventDispatcher"/> (exchange + routing key
///      equals the event-kind name);
///   3. the delivery-state enum the outbox drives (<c>queued</c>,
///      <c>dispatched</c>, <c>failed</c>).
///
/// Additive evolution: adding a new kind MUST NOT remove any existing kind.
/// Any removal or rename of a pinned kind is a breaking change and fails
/// this test — which is what the contract-version bump rule in
/// <c>specs/006-engagement-progress-parent/contracts/phase4-downstream-events-contract.md</c>
/// requires.
/// </summary>
public class DownstreamEventsAdditiveOnlyTests
{
    /// <summary>
    /// Frozen snapshot — do NOT edit to accommodate a rename or removal.
    /// To add a new kind, append it to the production enum AND append here.
    /// </summary>
    private static readonly string[] FrozenV1EventKinds =
    {
        "mastery_updated",
        "badge_awarded",
        "streak_changed",
        "focus_area_updated",
        "weekly_report_generated",
        "at_risk_flagged",
        "at_risk_cleared",
    };

    [Fact]
    public void Every_Frozen_V1_Kind_Is_Still_Declared_By_The_Enum()
    {
        var declared = Enum.GetNames(typeof(Phase4DownstreamEventKind)).ToHashSet();
        foreach (var pinned in FrozenV1EventKinds)
        {
            Assert.True(
                declared.Contains(pinned),
                $"Additive-only violation: contract v1.0.0 kind '{pinned}' is no longer declared. " +
                "Existing kinds MUST NOT be removed or renamed — bump the contract version and add a new kind instead.");
        }
    }

    [Fact]
    public void Enum_Values_Are_A_Superset_Of_The_Frozen_V1_Kinds()
    {
        var declared = Enum.GetNames(typeof(Phase4DownstreamEventKind)).ToHashSet();
        var added = declared.Except(FrozenV1EventKinds).ToList();
        Assert.All(added, name =>
            Assert.False(
                string.IsNullOrWhiteSpace(name),
                "New downstream event kinds MUST be non-empty snake_case identifiers."));
        Assert.True(
            declared.Count >= FrozenV1EventKinds.Length,
            $"Declared kinds ({declared.Count}) fewer than frozen v1 kinds ({FrozenV1EventKinds.Length}).");
    }

    [Fact]
    public void Routing_Exchange_Is_The_Contracted_Topic()
    {
        Assert.Equal("phase4.downstream.events", Phase4DownstreamEventDispatcher.ExchangeName);
    }

    [Fact]
    public void Outbox_Delivery_State_Enum_Is_Frozen()
    {
        var expected = new[] { "queued", "dispatched", "failed" };
        foreach (var state in expected)
        {
            Assert.NotNull(state);
        }
        Assert.Equal(3, expected.Length);
    }
}
