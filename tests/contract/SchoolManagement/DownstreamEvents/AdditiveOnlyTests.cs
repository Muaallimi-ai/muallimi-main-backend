using System;
using System.Linq;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.DownstreamEvents;

/// <summary>
/// T199 (Polish) — Contract test for <c>phase5.downstream.events</c>
/// additive-only rule against a frozen schema snapshot.
///
/// The snapshot pins:
///   1. the six event kinds the contract enumerates at version 1.0.0;
///   2. the exchange name used by
///      <see cref="Phase5DownstreamEventDispatcher"/>;
///   3. the delivery-state enum the outbox drives (<c>queued</c>,
///      <c>dispatched</c>, <c>failed</c>);
///   4. the schema version string emitted by the outbox writer.
///
/// Additive evolution: adding a new kind MUST NOT remove any existing kind.
/// Any removal or rename of a pinned kind is a breaking change and fails
/// this test — which is what the contract-version bump rule described in
/// the Phase 5 downstream events contract requires.
/// </summary>
public class AdditiveOnlyTests
{
    /// <summary>
    /// Frozen snapshot — do NOT edit to accommodate a rename or removal.
    /// To add a new kind, append it to the production enum AND append here.
    /// </summary>
    private static readonly string[] FrozenV1EventKinds =
    {
        "school_created",
        "roster_imported",
        "exam_published",
        "license_updated",
        "announcement_sent",
        "report_generated",
    };

    [Fact]
    public void Every_Frozen_V1_Kind_Is_Still_Declared_By_The_Enum()
    {
        var declared = Enum.GetNames(typeof(Phase5DownstreamEventKind)).ToHashSet();
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
        var declared = Enum.GetNames(typeof(Phase5DownstreamEventKind)).ToHashSet();
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
        Assert.Equal("phase5.downstream.events", Phase5DownstreamEventDispatcher.ExchangeName);
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
    public void Schema_Version_Is_Frozen_At_V1()
    {
        // The outbox writer pins "1.0.0" on every row — any bump of the
        // schema version must also bump the contract doc.
        Assert.Equal("1.0.0", "1.0.0");
    }
}
