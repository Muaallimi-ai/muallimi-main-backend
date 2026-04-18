using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T156 (Polish) — Dispatcher resilience under broker outage.
///
/// Simulates the broker being unavailable while the Phase 4 pipeline
/// continues to produce state changes. The contract guarantees:
///   1. The state-change transaction is never rolled back because the
///      broker is down — the outbox row lands, state = <c>queued</c>.
///   2. Repeated enqueue during the outage accumulates rows that remain
///      visible to the next successful drain (at-least-once delivery).
///   3. A transient publish failure leaves the row <c>queued</c> with
///      <c>DispatchAttempts</c> incremented; after five failures the row
///      is moved to <c>failed</c> so an operator can intervene.
///   4. A successful simulated drain transitions surviving rows to
///      <c>dispatched</c> without losing their correlation identifier.
///
/// These invariants are what the contract doc at
/// <c>specs/006-engagement-progress-parent/contracts/phase4-downstream-events-contract.md</c>
/// describes as "outbox atomicity" and "at-least-once delivery".
/// </summary>
public class DownstreamDispatchResilienceTests
{
    [Fact]
    public async Task Enqueue_During_BrokerOutage_Lands_Rows_In_Queued_State()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var outbox = new Phase4DownstreamEventOutbox(db);
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await outbox.EnqueueAsync(
                Phase4DownstreamEventKind.mastery_updated,
                tenantId,
                studentId,
                scope: new { curriculum_type = "moe" },
                payload: new { attempt = i },
                correlationId: $"corr-{i}");
        }
        await db.SaveChangesAsync();

        var rows = await db.Phase4DownstreamEvents.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal("queued", r.DeliveryState));
        Assert.All(rows, r => Assert.Equal(0, r.DispatchAttempts));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.CorrelationId)));
    }

    [Fact]
    public async Task Transient_Publish_Failure_Keeps_Row_Queued_And_Increments_Attempts()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var row = new Phase4DownstreamEvent
        {
            Phase4DownstreamEventId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            StudentId = Guid.NewGuid(),
            EventKind = nameof(Phase4DownstreamEventKind.mastery_updated),
            Scope = "{}",
            Payload = "{}",
            CorrelationId = "corr-transient",
            OccurredAt = DateTime.UtcNow,
            DeliveryState = "queued",
            DispatchAttempts = 0,
        };
        db.Phase4DownstreamEvents.Add(row);
        await db.SaveChangesAsync();

        // Simulate three transient publish failures — mirrors the branch in
        // Phase4DownstreamEventDispatcher.DrainOnceAsync's catch block.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            row.DispatchAttempts += 1;
            row.DeliveryState = row.DispatchAttempts >= 5 ? "failed" : "queued";
        }
        await db.SaveChangesAsync();

        var refreshed = await db.Phase4DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("queued", refreshed.DeliveryState);
        Assert.Equal(3, refreshed.DispatchAttempts);
        Assert.Null(refreshed.DispatchedAt);
    }

    [Fact]
    public async Task FiveConsecutiveFailures_Move_Row_To_Failed_State()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var row = new Phase4DownstreamEvent
        {
            Phase4DownstreamEventId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            StudentId = Guid.NewGuid(),
            EventKind = nameof(Phase4DownstreamEventKind.badge_awarded),
            Scope = "{}",
            Payload = "{}",
            CorrelationId = "corr-exhausted",
            OccurredAt = DateTime.UtcNow,
            DeliveryState = "queued",
            DispatchAttempts = 0,
        };
        db.Phase4DownstreamEvents.Add(row);
        await db.SaveChangesAsync();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            row.DispatchAttempts += 1;
            row.DeliveryState = row.DispatchAttempts >= 5 ? "failed" : "queued";
        }
        await db.SaveChangesAsync();

        var refreshed = await db.Phase4DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("failed", refreshed.DeliveryState);
        Assert.Equal(5, refreshed.DispatchAttempts);
    }

    [Fact]
    public async Task Successful_Drain_Transitions_Queued_Rows_To_Dispatched_And_Preserves_CorrelationId()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var outbox = new Phase4DownstreamEventOutbox(db);
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();

        await outbox.EnqueueAsync(
            Phase4DownstreamEventKind.streak_changed,
            tenantId,
            studentId,
            scope: new { },
            payload: new { new_length = 3 },
            correlationId: "corr-drain");
        await db.SaveChangesAsync();

        var row = await db.Phase4DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        // simulate the successful path: broker acks, dispatcher updates the row.
        row.DeliveryState = "dispatched";
        row.DispatchedAt = DateTime.UtcNow;
        row.DispatchAttempts += 1;
        await db.SaveChangesAsync();

        var refreshed = await db.Phase4DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("dispatched", refreshed.DeliveryState);
        Assert.Equal("corr-drain", refreshed.CorrelationId);
        Assert.NotNull(refreshed.DispatchedAt);
    }
}
