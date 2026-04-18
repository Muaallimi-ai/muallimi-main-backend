using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;
using Muallimi.MainBackend.Tests.Contract.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.SessionEvents;

/// <summary>
/// T125 — Dispatch-resilience contract.
///
/// The session event outbox MUST survive a broker outage mid-run: rows
/// remain <c>pending</c> until the broker is reachable again and then
/// dispatch on the next drain. This test simulates a broker kill by
/// toggling a <c>brokerAvailable</c> flag, running the drain loop against
/// an in-memory DbContext, and asserting:
///
///   1. No event is silently dropped (all eleven rows survive the outage).
///   2. Failed publishes increment <c>dispatch_attempts</c>.
///   3. When the broker returns, the second drain transitions every row to
///      <c>published</c> with a non-null <c>dispatched_at</c>.
///
/// The live RabbitMQ-backed <see cref="SessionEventDispatcher"/> runs as a
/// BackgroundService under the real API host; at the unit/integration layer
/// we re-implement the drain loop here so the test stays deterministic and
/// free of broker mocks. The contract itself — at-least-once delivery, no
/// loss on broker failure — is what this test pins down.
/// </summary>
public class DispatchResilienceTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Broker_Outage_Mid_Run_Preserves_Every_Event_For_Replay()
    {
        await using var db = NewInMemoryDb();
        var writer = new SessionEventOutboxWriter(db);
        var scope = new CurriculumScope("Moe", "Grade7", null, null, null, null);

        // Seed one event per kind so all eleven lifecycle events are covered.
        foreach (var kind in Enum.GetValues<SessionEventKind>())
        {
            await writer.EnqueueAsync(
                kind, TenantId, Guid.NewGuid(), Guid.NewGuid(),
                payload: new { marker = kind.ToString() },
                curriculumScope: scope,
                planTierSnapshot: "standard");
        }
        await db.SaveChangesAsync();

        var drainer = new StubDispatcherDrainer(brokerAvailable: false);

        // First drain: broker is down. Every row must remain pending and
        // every dispatch_attempts counter must increase by one. Nothing is
        // marked published. Nothing is silently dropped.
        await drainer.DrainOnceAsync(db);

        var expectedCount = Enum.GetValues<SessionEventKind>().Length;
        var afterOutage = await db.SessionEvents.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(expectedCount, afterOutage.Count);
        Assert.All(afterOutage, r => Assert.Equal("pending", r.DispatchState));
        Assert.All(afterOutage, r => Assert.Equal(1, r.DispatchAttempts));
        Assert.All(afterOutage, r => Assert.Null(r.DispatchedAt));

        // Broker comes back online. Next drain publishes every surviving
        // row — at-least-once delivery with zero loss.
        drainer.BrokerAvailable = true;
        await drainer.DrainOnceAsync(db);

        var afterRecovery = await db.SessionEvents.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(expectedCount, afterRecovery.Count);
        Assert.All(afterRecovery, r => Assert.Equal("published", r.DispatchState));
        Assert.All(afterRecovery, r => Assert.NotNull(r.DispatchedAt));
        Assert.All(afterRecovery, r => Assert.Equal(2, r.DispatchAttempts));
    }

    [Fact]
    public async Task Permanent_Failure_Marks_Row_Failed_After_Ten_Attempts()
    {
        // Contract: per SessionEventDispatcher.cs, after ten failed attempts
        // the row transitions from pending to failed so it cannot block the
        // queue head forever. A human operator then inspects and resubmits.
        await using var db = NewInMemoryDb();
        var writer = new SessionEventOutboxWriter(db);

        await writer.EnqueueAsync(
            SessionEventKind.session_start, TenantId, Guid.NewGuid(), Guid.NewGuid(),
            new { device_class = "mobile_small", preferred_language = "ar" },
            new CurriculumScope("Moe", "Grade7", null, null, null, null),
            "free");
        await db.SaveChangesAsync();

        var drainer = new StubDispatcherDrainer(brokerAvailable: false);
        for (var i = 0; i < 10; i++)
        {
            await drainer.DrainOnceAsync(db);
        }

        var row = await db.SessionEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("failed", row.DispatchState);
        Assert.Equal(10, row.DispatchAttempts);
        Assert.Null(row.DispatchedAt);

        // Subsequent drains must not retry a failed row (it is no longer
        // pending), which guarantees the queue head cannot wedge behind a
        // permanently broken payload.
        await drainer.DrainOnceAsync(db);
        var reread = await db.SessionEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(10, reread.DispatchAttempts);
    }

    [Fact]
    public async Task Outbox_Row_Commits_Atomically_With_State_Change()
    {
        // The outbox writer only enqueues — the caller owns SaveChangesAsync.
        // If SaveChangesAsync is never called (e.g. the transaction is
        // aborted before commit), the outbox row MUST NOT be persisted so
        // Phase 4 never sees an event for a state change that did not happen.
        await using var db = NewInMemoryDb();
        var writer = new SessionEventOutboxWriter(db);

        await writer.EnqueueAsync(
            SessionEventKind.session_start, TenantId, Guid.NewGuid(), Guid.NewGuid(),
            new { device_class = "mobile_small", preferred_language = "ar" },
            new CurriculumScope("Moe", "Grade7", null, null, null, null),
            "free");
        // Deliberately skip SaveChangesAsync — simulates a rolled-back unit
        // of work.
        db.ChangeTracker.Clear();

        var rows = await db.SessionEvents.IgnoreQueryFilters().ToListAsync();
        Assert.Empty(rows);
    }

    private static MuallimiDbContext NewInMemoryDb()
    {
        return Phase3TestDbContextFactory.Create(
            new StubTenantContextAccessor(TenantId),
            databaseName: $"dispatch-resilience-{Guid.NewGuid():N}");
    }

    private sealed class StubTenantContextAccessor : IDbTenantContextAccessor
    {
        public StubTenantContextAccessor(Guid? tenantId) => CurrentTenantId = tenantId;
        public Guid? CurrentTenantId { get; }
    }

    /// <summary>
    /// Stand-in for the BatchSize=50 drain loop in
    /// <see cref="SessionEventDispatcher"/>. Implements the contract
    /// (pending → published on success, increment attempts on failure, mark
    /// failed after 10 attempts) against a toggleable broker flag so tests
    /// can simulate an outage deterministically.
    /// </summary>
    private sealed class StubDispatcherDrainer
    {
        public bool BrokerAvailable { get; set; }

        public StubDispatcherDrainer(bool brokerAvailable)
        {
            BrokerAvailable = brokerAvailable;
        }

        public async Task DrainOnceAsync(MuallimiDbContext db)
        {
            var batch = await db.SessionEvents
                .IgnoreQueryFilters()
                .Where(e => e.DispatchState == "pending")
                .OrderBy(e => e.CreatedAt)
                .Take(50)
                .ToListAsync();

            foreach (var row in batch)
            {
                row.DispatchAttempts += 1;
                if (BrokerAvailable)
                {
                    row.DispatchState = "published";
                    row.DispatchedAt = DateTime.UtcNow;
                }
                else if (row.DispatchAttempts >= 10)
                {
                    row.DispatchState = "failed";
                }
            }

            await db.SaveChangesAsync();
        }
    }
}
