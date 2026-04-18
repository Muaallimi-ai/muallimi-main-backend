using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.WeeklyReports;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T087 (US3) — Integration test for regeneration idempotency.
///
/// Regenerating a report reuses the same row (UNIQUE constraint
/// preserved) but issues a fresh <c>run_id</c> and re-runs through the
/// Phase 2 guardrail chain. The downstream outbox receives a second
/// <c>weekly_report_generated</c> event for the same report id.
/// Regeneration does NOT produce a duplicate ready row.
/// </summary>
public class WeeklyReportRegenerationTests
{
    [Fact]
    public async Task Regeneration_Updates_Run_Id_And_Emits_A_Fresh_Event()
    {
        var harness = new WeeklyReportTestHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var first = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end, "corr-1", forceRegenerate: false);
        var firstReport = await harness.Reports.GetByIdAsync(tenantId, first.WeeklyReportId);
        var firstRunId = firstReport!.RunId;

        var regenerated = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end, "corr-2", forceRegenerate: true);
        Assert.Equal(WeeklyReportGenerationOutcome.Regenerated, regenerated.Outcome);
        Assert.Equal(first.WeeklyReportId, regenerated.WeeklyReportId);

        // Re-fetch with AsNoTracking so we pick up the persisted run id,
        // not any tracked-copy lingering from the first load.
        var refreshed = await harness.Db.WeeklyReports
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(r => r.WeeklyReportId == regenerated.WeeklyReportId);
        Assert.NotEqual(firstRunId, refreshed.RunId);
        Assert.Equal("ready", refreshed.Status);

        var events = await harness.Db.Phase4DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.EventKind == Phase4DownstreamEventKind.weekly_report_generated.ToString())
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(tenantId, e.TenantId));

        var rows = await harness.Db.WeeklyReports
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.StudentId == studentId)
            .ToListAsync();
        Assert.Single(rows);
    }
}
