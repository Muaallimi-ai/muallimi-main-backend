using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.Phase4EventConsumer;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SchoolManagement;

/// <summary>
/// T086 (US4) — Integration test for Phase 4 event consumption into
/// <see cref="SchoolAggregateView"/>.
///
/// Drives <see cref="SchoolAggregateViewUpdater.ApplyAsync"/> with a
/// synthetic Phase 4 envelope and asserts:
///   • aggregate rows appear for the impacted school (scope_type='class'
///     and 'school');
///   • the rows carry the envelope's event id (last_event_id) so a
///     second dispatch with the same id is a no-op;
///   • a second dispatch with a different id updates last_event_id.
/// </summary>
public class Phase4EventConsumptionTests
{
    [Fact]
    public async Task Event_Creates_Class_And_School_Aggregate_Rows()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var updater = harness.BuildUpdater();
        var eventId = Guid.NewGuid();

        await updater.ApplyAsync(new Phase4DownstreamEventEnvelope
        {
            EventId = eventId,
            TenantId = DashboardHarness.TenantAlpha,
            EventKind = "mastery_updated",
            StudentId = harness.AlphaStudentIds[0],
            Scope = "{}",
            Payload = "{}",
            CorrelationId = "corr-t086",
            OccurredAt = DateTime.UtcNow,
        }, CancellationToken.None);

        var classRow = await db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(v =>
                v.TenantId == DashboardHarness.TenantAlpha
                && v.ScopeType == "class"
                && v.ScopeId == DashboardHarness.ClassAlpha);
        Assert.NotNull(classRow);
        Assert.Equal(3, classRow!.ActiveStudentCount);
        Assert.Equal(1, classRow.AtRiskCount);
        Assert.Equal(eventId, classRow.LastEventId);

        var schoolRows = await db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(v => v.TenantId == DashboardHarness.TenantAlpha && v.ScopeType == "school")
            .ToListAsync();
        Assert.NotEmpty(schoolRows);
        Assert.All(schoolRows, r => Assert.Equal(eventId, r.LastEventId));
    }

    [Fact]
    public async Task Replay_Of_Same_Event_Id_Is_Idempotent()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var updater = harness.BuildUpdater();
        var eventId = Guid.NewGuid();
        var envelope = new Phase4DownstreamEventEnvelope
        {
            EventId = eventId,
            TenantId = DashboardHarness.TenantAlpha,
            EventKind = "mastery_updated",
            StudentId = harness.AlphaStudentIds[0],
            Scope = "{}",
            Payload = "{}",
            CorrelationId = "corr-t086",
            OccurredAt = DateTime.UtcNow,
        };

        await updater.ApplyAsync(envelope, CancellationToken.None);
        var countAfterFirst = await db.SchoolAggregateViews.IgnoreQueryFilters().CountAsync();

        await updater.ApplyAsync(envelope, CancellationToken.None);
        var countAfterReplay = await db.SchoolAggregateViews.IgnoreQueryFilters().CountAsync();

        Assert.Equal(countAfterFirst, countAfterReplay);
    }

    [Fact]
    public async Task Subsequent_Event_Advances_Last_Event_Id()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var updater = harness.BuildUpdater();
        var firstEventId = Guid.NewGuid();
        var secondEventId = Guid.NewGuid();

        await updater.ApplyAsync(new Phase4DownstreamEventEnvelope
        {
            EventId = firstEventId,
            TenantId = DashboardHarness.TenantAlpha,
            EventKind = "mastery_updated",
            StudentId = harness.AlphaStudentIds[0],
            OccurredAt = DateTime.UtcNow,
        }, CancellationToken.None);

        await updater.ApplyAsync(new Phase4DownstreamEventEnvelope
        {
            EventId = secondEventId,
            TenantId = DashboardHarness.TenantAlpha,
            EventKind = "badge_awarded",
            StudentId = harness.AlphaStudentIds[1],
            OccurredAt = DateTime.UtcNow,
        }, CancellationToken.None);

        var classRow = await db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(v =>
                v.TenantId == DashboardHarness.TenantAlpha
                && v.ScopeType == "class"
                && v.ScopeId == DashboardHarness.ClassAlpha);
        Assert.Equal(secondEventId, classRow.LastEventId);
    }
}
