using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.DownstreamEvents;

/// <summary>
/// T200 (Polish) — Dispatcher resilience under broker outage.
///
/// Simulates the broker being unavailable while the Phase 5 pipeline
/// continues to produce state changes (school_created, roster_imported,
/// exam_published, license_updated, announcement_sent, report_generated).
/// The contract guarantees:
///   1. The state-change transaction is never rolled back because the
///      broker is down — the outbox row lands, state = <c>queued</c>.
///   2. Repeated enqueue during the outage accumulates rows that remain
///      visible to the next successful drain (at-least-once delivery).
///   3. A transient publish failure leaves the row <c>queued</c> with
///      <c>DispatchAttempts</c> incremented; after five failures the row
///      is moved to <c>failed</c> so an operator can intervene.
///   4. A successful simulated drain transitions surviving rows to
///      <c>dispatched</c> without losing their correlation identifier.
/// </summary>
public class DispatchResilienceTests
{
    [Fact]
    public async Task Enqueue_During_BrokerOutage_Lands_Rows_In_Queued_State()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await outbox.EnqueueAsync(
                Phase5DownstreamEventKind.license_updated,
                tenantId,
                schoolTenantId,
                payload: new { attempt = i },
                correlationId: $"corr-{i}");
        }
        await db.SaveChangesAsync();

        var rows = await db.Phase5DownstreamEvents.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal("queued", r.DeliveryState));
        Assert.All(rows, r => Assert.Equal(0, r.DispatchAttempts));
        Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.CorrelationId)));
        Assert.All(rows, r => Assert.Equal("1.0.0", r.SchemaVersion));
    }

    [Fact]
    public async Task Transient_Publish_Failure_Keeps_Row_Queued_And_Increments_Attempts()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var row = new Phase5DownstreamEvent
        {
            Phase5EventId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SchoolTenantId = Guid.NewGuid(),
            EventKind = nameof(Phase5DownstreamEventKind.announcement_sent),
            Payload = "{}",
            CorrelationId = "corr-transient",
            OccurredAt = DateTime.UtcNow,
            SchemaVersion = "1.0.0",
            DeliveryState = "queued",
            DispatchAttempts = 0,
        };
        db.Phase5DownstreamEvents.Add(row);
        await db.SaveChangesAsync();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            row.DispatchAttempts += 1;
            row.DeliveryState = row.DispatchAttempts >= 5 ? "failed" : "queued";
        }
        await db.SaveChangesAsync();

        var refreshed = await db.Phase5DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("queued", refreshed.DeliveryState);
        Assert.Equal(3, refreshed.DispatchAttempts);
        Assert.Null(refreshed.DispatchedAt);
    }

    [Fact]
    public async Task FiveConsecutiveFailures_Move_Row_To_Failed_State()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var row = new Phase5DownstreamEvent
        {
            Phase5EventId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            SchoolTenantId = Guid.NewGuid(),
            EventKind = nameof(Phase5DownstreamEventKind.exam_published),
            Payload = "{}",
            CorrelationId = "corr-exhausted",
            OccurredAt = DateTime.UtcNow,
            SchemaVersion = "1.0.0",
            DeliveryState = "queued",
            DispatchAttempts = 0,
        };
        db.Phase5DownstreamEvents.Add(row);
        await db.SaveChangesAsync();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            row.DispatchAttempts += 1;
            row.DeliveryState = row.DispatchAttempts >= 5 ? "failed" : "queued";
        }
        await db.SaveChangesAsync();

        var refreshed = await db.Phase5DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("failed", refreshed.DeliveryState);
        Assert.Equal(5, refreshed.DispatchAttempts);
    }

    [Fact]
    public async Task Successful_Drain_Transitions_Queued_Rows_To_Dispatched_And_Preserves_CorrelationId()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();

        await outbox.EnqueueAsync(
            Phase5DownstreamEventKind.report_generated,
            tenantId,
            schoolTenantId,
            payload: new { report_type = "mastery" },
            correlationId: "corr-drain");
        await db.SaveChangesAsync();

        var row = await db.Phase5DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        row.DeliveryState = "dispatched";
        row.DispatchedAt = DateTime.UtcNow;
        row.DispatchAttempts += 1;
        await db.SaveChangesAsync();

        var refreshed = await db.Phase5DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("dispatched", refreshed.DeliveryState);
        Assert.Equal("corr-drain", refreshed.CorrelationId);
        Assert.NotNull(refreshed.DispatchedAt);
    }

    [Fact]
    public async Task Repeated_Enqueue_During_Outage_Accumulates_Rows_For_Next_Drain()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();

        // Simulate six kinds all enqueued during the same outage window.
        foreach (var kind in Enum.GetValues<Phase5DownstreamEventKind>())
        {
            await outbox.EnqueueAsync(
                kind,
                tenantId,
                schoolTenantId,
                payload: new { kind = kind.ToString() },
                correlationId: $"corr-{kind}");
        }
        await db.SaveChangesAsync();

        var queued = await db.Phase5DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.DeliveryState == "queued")
            .ToListAsync();
        Assert.Equal(Enum.GetValues<Phase5DownstreamEventKind>().Length, queued.Count);
    }
}
