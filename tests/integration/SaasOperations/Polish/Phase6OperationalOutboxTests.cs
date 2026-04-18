using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Polish;

/// <summary>
/// T132 (Polish) — Verify <see cref="Phase6OperationalEventOutbox"/> accepts
/// all seven event kinds declared by the Phase 6 downstream-events contract
/// (<c>subscription_created</c>, <c>payment_processed</c>, <c>payment_failed</c>,
/// <c>alert_fired</c>, <c>incident_opened</c>, <c>incident_resolved</c>,
/// <c>data_deletion_completed</c>) and that every row carries a
/// correlation_id.
///
/// The downstream dispatcher (<see cref="Phase6OperationalEventDispatcher"/>)
/// drains rows whose <c>DispatchedAt</c> is null — unset at insert time.
/// This suite asserts the insert side of the contract is sound; drain
/// verification is covered by the dispatcher's own integration tests.
/// </summary>
public class Phase6OperationalOutboxTests
{
    [Theory]
    [InlineData("subscription_created")]
    [InlineData("payment_processed")]
    [InlineData("payment_failed")]
    [InlineData("alert_fired")]
    [InlineData("incident_opened")]
    [InlineData("incident_resolved")]
    [InlineData("data_deletion_completed")]
    public async Task EnqueueAsync_writes_row_with_correlation_id_and_no_dispatch_timestamp(string kind)
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var outbox = new Phase6OperationalEventOutbox(db);
        var tenantId = Guid.NewGuid();
        var correlationId = $"corr-{Guid.NewGuid():N}";

        await outbox.EnqueueAsync(tenantId, kind, new { sample = true }, correlationId);

        var row = await db.Phase6OperationalEvents
            .Where(e => e.EventKind == kind)
            .SingleAsync();

        Assert.Equal(kind, row.EventKind);
        Assert.Equal(tenantId, row.TenantId);
        Assert.Equal(correlationId, row.CorrelationId);
        Assert.Null(row.DispatchedAt);
        Assert.Equal("1.0.0", row.SchemaVersion);
    }

    [Fact]
    public async Task All_seven_kinds_coexist_in_outbox_in_insertion_order()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var outbox = new Phase6OperationalEventOutbox(db);
        var tenantId = Guid.NewGuid();

        var kinds = new[]
        {
            "subscription_created",
            "payment_processed",
            "payment_failed",
            "alert_fired",
            "incident_opened",
            "incident_resolved",
            "data_deletion_completed"
        };
        foreach (var k in kinds)
        {
            await outbox.EnqueueAsync(tenantId, k, new { kind = k }, $"corr-{k}");
        }

        var stored = await db.Phase6OperationalEvents
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.EventKind)
            .ToListAsync();
        Assert.Equal(kinds, stored);
    }

    [Fact]
    public async Task Enqueue_preserves_object_payload_as_json()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var outbox = new Phase6OperationalEventOutbox(db);
        var tenantId = Guid.NewGuid();

        await outbox.EnqueueAsync(
            tenantId,
            "payment_processed",
            new { amount = 99, currency = "egp" },
            "corr-payload");

        var row = await db.Phase6OperationalEvents.SingleAsync();
        Assert.Contains("\"amount\":99", row.Payload);
        Assert.Contains("\"currency\":\"egp\"", row.Payload);
    }

    [Fact]
    public void ExchangeName_matches_contract()
    {
        // Catalogue lock — if a deploy accidentally renames the exchange,
        // Phase 6 dashboards and Phase 5+6 downstream consumers break.
        Assert.Equal("phase6.operational.events", Phase6OperationalEventOutbox.ExchangeName);
    }
}
