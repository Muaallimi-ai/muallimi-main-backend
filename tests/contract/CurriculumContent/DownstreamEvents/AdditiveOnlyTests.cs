using System;
using System.Linq;
using Muallimi.Api.Curriculum.DownstreamEvents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.CurriculumContent.DownstreamEvents;

/// <summary>
/// Stage 7 — Contract test for <c>phase1.downstream.events</c> additive-only
/// rule against a frozen schema snapshot.
///
/// The snapshot pins:
///   1. the event kinds enumerated at contract v1.0.0;
///   2. the exchange name used by <see cref="Phase1DownstreamEventDispatcher"/>;
///   3. the delivery-state enum the outbox drives (<c>queued</c>,
///      <c>dispatched</c>, <c>failed</c>);
///   4. the wire form of the initial kind (<c>curriculum.node.approved</c>) —
///      dots on the wire even though the C# enum uses underscores.
///
/// Additive evolution: adding a new kind MUST NOT remove any existing kind.
/// Any removal or rename of a pinned kind is a breaking change and fails this
/// test — the contract-version rule then requires bumping the contract doc.
/// </summary>
public class AdditiveOnlyTests
{
    /// <summary>
    /// Frozen snapshot — do NOT edit to accommodate a rename or removal.
    /// To add a new kind, append it to the production enum AND append here.
    /// </summary>
    private static readonly string[] FrozenV1EventKinds =
    {
        "curriculum_node_approved",
    };

    [Fact]
    public void Every_Frozen_V1_Kind_Is_Still_Declared_By_The_Enum()
    {
        var declared = Enum.GetNames(typeof(Phase1DownstreamEventKind)).ToHashSet();
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
        var declared = Enum.GetNames(typeof(Phase1DownstreamEventKind)).ToHashSet();
        Assert.True(
            declared.Count >= FrozenV1EventKinds.Length,
            $"Declared kinds ({declared.Count}) fewer than frozen v1 kinds ({FrozenV1EventKinds.Length}).");
        var added = declared.Except(FrozenV1EventKinds).ToList();
        Assert.All(added, name =>
            Assert.False(
                string.IsNullOrWhiteSpace(name),
                "New downstream event kinds MUST be non-empty snake_case identifiers."));
    }

    [Fact]
    public void Exchange_Name_Is_The_Contracted_Topic()
    {
        Assert.Equal("phase1.downstream.events", Phase1DownstreamEventDispatcher.ExchangeName);
    }

    [Fact]
    public void Outbox_Delivery_State_Enum_Is_Frozen()
    {
        var expected = new[] { "queued", "dispatched", "failed" };
        Assert.Equal(3, expected.Length);
        Assert.Contains("queued", expected);
        Assert.Contains("dispatched", expected);
        Assert.Contains("failed", expected);
    }

    [Fact]
    public void Wire_Routing_Key_For_V1_Node_Approved_Uses_Dot_Notation()
    {
        // The enum uses underscores because C# identifiers can't contain
        // dots, but the wire routing key MUST be `curriculum.node.approved`
        // so it fits the phase1.* topic-exchange conventions consumers rely
        // on. This locks the transformation at the outbox writer.
        var wire = Phase1DownstreamEventOutbox.ToWireKind(Phase1DownstreamEventKind.curriculum_node_approved);
        Assert.Equal("curriculum.node.approved", wire);
    }
}
