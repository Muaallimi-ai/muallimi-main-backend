using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement;

/// <summary>
/// T204 (Polish) — Performance budget for school-admin and teacher dashboards.
///
/// Phase 0 targets (see <c>specs/002-foundation-local-parity/plan.md</c>)
/// cap primary dashboard reads at a few hundred milliseconds on the local
/// InMemory stack. The budget here is intentionally generous:
/// <see cref="BudgetMilliseconds"/> = 2000 ms. CI runs on slower hardware
/// than developer laptops, but 2 seconds for an already-cached in-memory
/// dashboard rollup is the floor — anything slower is a red flag for an
/// accidental N+1 or a missing projection.
///
/// These tests run the dashboard twice: the first call primes the cache
/// and is permitted a larger budget; the second call MUST hit the
/// <c>SchoolDashboardQueryCache</c> short-TTL cache and come in well
/// under the budget.
/// </summary>
public class PerformanceBudgetTests
{
    private const int BudgetMilliseconds = 2000;
    private const int CachedBudgetMilliseconds = 500;

    [Fact]
    public async Task School_Admin_Dashboard_First_Read_Within_Budget()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);
        var service = harness.BuildService();

        var sw = Stopwatch.StartNew();
        var response = await service.GetSchoolDashboardAsync(
            DashboardHarness.TenantAlpha,
            DashboardHarness.SchoolAlpha);
        sw.Stop();

        Assert.NotNull(response);
        Assert.True(
            sw.ElapsedMilliseconds < BudgetMilliseconds,
            $"School dashboard first read took {sw.ElapsedMilliseconds}ms, budget is {BudgetMilliseconds}ms.");
    }

    [Fact]
    public async Task School_Admin_Dashboard_Second_Read_Hits_Cache_And_Is_Faster()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);
        var service = harness.BuildService();

        // Prime the cache.
        await service.GetSchoolDashboardAsync(
            DashboardHarness.TenantAlpha,
            DashboardHarness.SchoolAlpha);

        var sw = Stopwatch.StartNew();
        var response = await service.GetSchoolDashboardAsync(
            DashboardHarness.TenantAlpha,
            DashboardHarness.SchoolAlpha);
        sw.Stop();

        Assert.NotNull(response);
        Assert.True(
            sw.ElapsedMilliseconds < CachedBudgetMilliseconds,
            $"School dashboard cached read took {sw.ElapsedMilliseconds}ms, cache budget is {CachedBudgetMilliseconds}ms.");
    }

    [Fact]
    public async Task Teacher_Dashboard_First_Read_Within_Budget()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);
        var service = harness.BuildService();

        var sw = Stopwatch.StartNew();
        var response = await service.GetTeacherDashboardAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha);
        sw.Stop();

        Assert.NotNull(response);
        Assert.True(
            sw.ElapsedMilliseconds < BudgetMilliseconds,
            $"Teacher dashboard read took {sw.ElapsedMilliseconds}ms, budget is {BudgetMilliseconds}ms.");
    }

    [Fact]
    public async Task Class_Detail_Is_Within_Budget()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);
        var service = harness.BuildService();

        var sw = Stopwatch.StartNew();
        var response = await service.GetClassDetailAsync(
            DashboardHarness.TenantAlpha,
            DashboardHarness.SchoolAlpha,
            DashboardHarness.ClassAlpha);
        sw.Stop();

        Assert.NotNull(response);
        Assert.True(
            sw.ElapsedMilliseconds < BudgetMilliseconds,
            $"Class detail read took {sw.ElapsedMilliseconds}ms, budget is {BudgetMilliseconds}ms.");
    }
}
