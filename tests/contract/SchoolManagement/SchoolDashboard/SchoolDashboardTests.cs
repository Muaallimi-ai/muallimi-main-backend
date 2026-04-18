using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.SchoolDashboard;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolDashboard;

/// <summary>
/// T084 (US4) — Contract test for GET /api/school-admin/dashboard.
///
/// Pins the route constant and verifies the response shape:
///   • top-level school metadata;
///   • mastery rows bucketed by (grade, subject_id) with mastery band
///     classified by the band thresholds;
///   • engagement aggregate (active_streak_count, badges_awarded_count);
///   • at-risk summary (total + per-class).
/// The test drives <see cref="SchoolDashboardService"/> directly so the
/// shape is validated without spinning the full WebApplication — follows
/// the Phase 5 contract-test convention.
/// </summary>
public class SchoolDashboardTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/school-admin/dashboard", SchoolDashboardEndpoints.DashboardRoute);
    }

    [Fact]
    public async Task Dashboard_Returns_School_Metadata_And_Mastery_Aggregates()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync();

        var service = harness.BuildService();
        await service.RebuildForSchoolAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolAlpha, Guid.NewGuid(), CancellationToken.None);

        var response = await service.GetSchoolDashboardAsync(DashboardHarness.TenantAlpha, DashboardHarness.SchoolAlpha, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(DashboardHarness.SchoolAlpha, response!.SchoolTenantId);
        Assert.Equal("مدرسة ألفا", response.SchoolNameAr);
        Assert.Equal("Alpha School", response.SchoolNameEn);
        Assert.Equal(3, response.TotalStudents);

        Assert.NotEmpty(response.SchoolMastery);
        var mathRow = response.SchoolMastery.FirstOrDefault(m => m.SubjectId == DashboardHarness.SubjectMath.ToString());
        Assert.NotNull(mathRow);
        Assert.InRange(mathRow!.AverageMastery, 0.40m, 0.90m);
        Assert.Contains(mathRow.MasteryBand, new[] { "introduced", "practicing", "on_track", "confident" });

        Assert.True(response.Engagement.ActiveStreakCount >= 0);
        Assert.True(response.Engagement.BadgesAwardedCount >= 1);

        Assert.Equal(1, response.AtRisk.TotalAtRisk);
        Assert.Single(response.AtRisk.PerClass);
        Assert.Equal(DashboardHarness.ClassAlpha, response.AtRisk.PerClass[0].ClassGroupId);
    }

    [Fact]
    public async Task Dashboard_Returns_Null_When_School_Does_Not_Exist()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        var service = harness.BuildService();

        var response = await service.GetSchoolDashboardAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public void Mastery_Band_Classifier_Is_Stable()
    {
        Assert.Equal("introduced", SchoolDashboardService.ClassifyBand(0.10m));
        Assert.Equal("practicing", SchoolDashboardService.ClassifyBand(0.35m));
        Assert.Equal("on_track", SchoolDashboardService.ClassifyBand(0.60m));
        Assert.Equal("confident", SchoolDashboardService.ClassifyBand(0.90m));
    }
}
