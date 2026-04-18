using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.WeeklyReports;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T086 (US3) — Integration test for one-report-per-child-per-window
/// uniqueness.
///
/// Two consecutive generator calls for the same
/// <c>(tenant_id, student_id, window_start, window_end)</c> MUST leave
/// exactly one row in <c>ready</c> status. The second call is an
/// idempotent no-op that returns the existing report id without writing
/// a duplicate row.
/// </summary>
public class WeeklyReportUniquenessTests
{
    [Fact]
    public async Task Two_Generator_Calls_For_Same_Window_Produce_A_Single_Ready_Row()
    {
        var harness = new WeeklyReportTestHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var first = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end, "corr-first", forceRegenerate: false);
        var second = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end, "corr-second", forceRegenerate: false);

        Assert.Equal(WeeklyReportGenerationOutcome.Generated, first.Outcome);
        Assert.Equal(WeeklyReportGenerationOutcome.Ready, second.Outcome);
        Assert.Equal(first.WeeklyReportId, second.WeeklyReportId);

        var rows = await harness.Db.WeeklyReports
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.StudentId == studentId)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("ready", rows[0].Status);
    }

    [Fact]
    public async Task Same_Window_Across_Different_Tenants_Is_Not_A_Conflict()
    {
        var harness = new WeeklyReportTestHarness();
        var studentIdShared = Guid.NewGuid();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var start = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var reportA = await harness.Generator.GenerateAsync(
            tenantA, studentIdShared, start, end, "corr-A", forceRegenerate: false);
        var reportB = await harness.Generator.GenerateAsync(
            tenantB, studentIdShared, start, end, "corr-B", forceRegenerate: false);

        Assert.Equal(WeeklyReportGenerationOutcome.Generated, reportA.Outcome);
        Assert.Equal(WeeklyReportGenerationOutcome.Generated, reportB.Outcome);
        Assert.NotEqual(reportA.WeeklyReportId, reportB.WeeklyReportId);
    }
}
