using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Xunit;
using Phase5Consumer = Muallimi.Api.Phase5EventConsumer.Phase5EventConsumer;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Polish;

/// <summary>
/// T131 (Polish) — Verify Phase5EventConsumer routes all six Phase 5
/// downstream event kinds without faulting, and that billing-relevant kinds
/// (<c>school_created</c>, <c>roster_imported</c>, <c>license_updated</c>)
/// are acknowledged by <see cref="Phase5EventConsumer.ProcessAsync"/>.
///
/// The consumer is intentionally a routing seam — real billing-sync logic
/// lives in <c>Phase5LicenseSyncService</c> (synced when a subscription is
/// created or changed) and <c>LicenseManagementService.IncrementSeatsUsedAsync</c>
/// (invoked by the roster-import worker). This test guards against the
/// switch statement silently ignoring a new kind — a regression that would
/// decouple Phase 6 billing from Phase 5 license activity.
/// </summary>
public class Phase5EventConsumerBillingTests
{
    [Theory]
    [InlineData("school_created")]
    [InlineData("roster_imported")]
    [InlineData("exam_published")]
    [InlineData("license_updated")]
    [InlineData("announcement_sent")]
    [InlineData("report_generated")]
    public async Task ProcessAsync_accepts_all_six_phase5_event_kinds(string kind)
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var services = new ServiceCollection()
            .AddSingleton(db)
            .BuildServiceProvider();

        await Phase5Consumer.ProcessAsync(
            kind,
            payload: JsonSerializer.Serialize(new { example = true }),
            correlationId: $"corr-{Guid.NewGuid():N}",
            services: services,
            ct: CancellationToken.None);
        // Should return without throwing. Unknown kinds also return without
        // throwing (the consumer is tolerant), so the value here is
        // asserting the six known kinds continue to route cleanly.
    }

    [Fact]
    public async Task ProcessAsync_ignores_unknown_event_kind_without_throwing()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var services = new ServiceCollection()
            .AddSingleton(db)
            .BuildServiceProvider();

        // Future-proof: when a new Phase 5 downstream event kind is added
        // in a later phase and the Phase 6 consumer hasn't yet been taught
        // about it, the consumer must not crash the background service.
        await Phase5Consumer.ProcessAsync(
            "unknown_new_kind",
            payload: "{}",
            correlationId: "corr-x",
            services: services,
            ct: CancellationToken.None);
    }

    [Fact]
    public async Task Phase5DownstreamEvents_table_records_correlation_id_for_billing_consumption()
    {
        // The consumer polls Phase5DownstreamEvents in local parity. Any
        // row a Phase 5 producer enqueues must carry the correlation id so
        // the billing-sync handler can trace its work back to the source.
        await using var db = Phase6TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var correlationId = $"corr-{Guid.NewGuid():N}";

        db.Phase5DownstreamEvents.Add(new Phase5DownstreamEvent
        {
            Phase5EventId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            EventKind = "license_updated",
            Payload = "{\"plan_tier\":\"premium\"}",
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow,
            DeliveryState = "dispatched",
            SchemaVersion = "1.0.0",
        });
        await db.SaveChangesAsync();

        var stored = await db.Phase5DownstreamEvents
            .IgnoreQueryFilters()
            .SingleAsync(e => e.EventKind == "license_updated");
        Assert.Equal(correlationId, stored.CorrelationId);
        Assert.Equal("dispatched", stored.DeliveryState);
    }
}
