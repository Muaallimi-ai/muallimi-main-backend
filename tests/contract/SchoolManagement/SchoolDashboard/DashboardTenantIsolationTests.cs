using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolDashboard;

/// <summary>
/// T087 (US4) — Tenant isolation for the school dashboard surfaces.
///
/// Seeds two tenants with their own school, class, students, Phase 4 state,
/// and at-risk flag. Runs the dashboard for Alpha and asserts no Beta rows
/// leak (no student ids, no class ids, no aggregate view rows). Also runs
/// the class-detail for a Beta class under Alpha's tenant and asserts the
/// query returns null (cross-tenant reads are forbidden).
/// </summary>
public class DashboardTenantIsolationTests
{
    [Fact]
    public async Task Dashboard_Does_Not_Leak_Rows_Across_Tenants()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync();

        var service = harness.BuildService();
        await service.RebuildForSchoolAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolAlpha, Guid.NewGuid(), CancellationToken.None);
        await service.RebuildForSchoolAsync(DashboardHarness.TenantBeta, DashboardHarness.SchoolBeta, Guid.NewGuid(), CancellationToken.None);

        var alphaDashboard = await service.GetSchoolDashboardAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolAlpha, CancellationToken.None);
        Assert.NotNull(alphaDashboard);
        Assert.Equal(DashboardHarness.SchoolAlpha, alphaDashboard!.SchoolTenantId);
        Assert.DoesNotContain(alphaDashboard.AtRisk.PerClass, pc => pc.ClassGroupId == DashboardHarness.ClassBeta);

        var crossTenantAttempt = await service.GetSchoolDashboardAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolBeta, CancellationToken.None);
        Assert.Null(crossTenantAttempt);

        var alphaClassDetail = await service.GetClassDetailAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolAlpha, DashboardHarness.ClassAlpha, CancellationToken.None);
        Assert.NotNull(alphaClassDetail);
        Assert.DoesNotContain(alphaClassDetail!.Students, s => harness.BetaStudentIds.Contains(s.StudentId));

        var betaClassFromAlphaTenant = await service.GetClassDetailAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolAlpha, DashboardHarness.ClassBeta, CancellationToken.None);
        Assert.Null(betaClassFromAlphaTenant);
    }

    [Fact]
    public async Task Aggregate_View_Rows_Are_Tenant_Partitioned()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync();

        var service = harness.BuildService();
        await service.RebuildForSchoolAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolAlpha, Guid.NewGuid(), CancellationToken.None);
        await service.RebuildForSchoolAsync(DashboardHarness.TenantBeta, DashboardHarness.SchoolBeta, Guid.NewGuid(), CancellationToken.None);

        await TenantIsolationHarness.AssertNoCrossTenantLeakAsync<SchoolAggregateView>(
            db,
            d => d.SchoolAggregateViews.IgnoreQueryFilters().Where(v => v.TenantId == DashboardHarness.TenantAlpha),
            DashboardHarness.TenantAlpha);

        await TenantIsolationHarness.AssertNoCrossTenantLeakAsync<SchoolAggregateView>(
            db,
            d => d.SchoolAggregateViews.IgnoreQueryFilters().Where(v => v.TenantId == DashboardHarness.TenantBeta),
            DashboardHarness.TenantBeta);
    }
}
