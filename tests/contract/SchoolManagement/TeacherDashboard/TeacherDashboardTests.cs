using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.TeacherDashboard;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.TeacherDashboard;

/// <summary>
/// T102 (US5) — Contract test for GET /api/teacher/dashboard.
///
/// Pins the route constant and verifies the response shape:
///   • assigned_classes list projects only the (class, subject) pairs the
///     teacher is actively assigned to;
///   • each row carries student_count, average_mastery, at_risk_count;
///   • a teacher with no active assignments gets an empty list (200, not
///     404) so the client distinguishes "no classes yet" from "unknown
///     teacher";
///   • an unknown teacher returns null → endpoint maps to 404.
/// </summary>
public class TeacherDashboardTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/teacher/dashboard", TeacherDashboardEndpoints.DashboardRoute);
    }

    [Fact]
    public async Task Dashboard_Returns_Only_Assigned_Class_Subject_Pairs()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetTeacherDashboardAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(TeacherDashboardHarness.TeacherMathAlpha, response!.TeacherId);
        Assert.Single(response.AssignedClasses);
        var row = response.AssignedClasses[0];
        Assert.Equal(TeacherDashboardHarness.ClassAlpha, row.ClassGroupId);
        Assert.Equal(TeacherDashboardHarness.SubjectMath, row.SubjectId);
        Assert.Equal(3, row.StudentCount);
        Assert.Equal(1, row.AtRiskCount);
        Assert.InRange(row.AverageMastery, 0.4m, 0.8m);
        Assert.Equal("الرياضيات", row.SubjectNameAr);
        Assert.Equal("Mathematics", row.SubjectNameEn);
    }

    [Fact]
    public async Task Dashboard_Returns_Empty_List_For_Unassigned_Teacher()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetTeacherDashboardAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherUnassignedAlpha,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Empty(response!.AssignedClasses);
    }

    [Fact]
    public async Task Dashboard_Returns_Null_For_Unknown_Teacher()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetTeacherDashboardAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Dashboard_Does_Not_Leak_Cross_Tenant_Classes()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: true);

        var service = harness.BuildService();

        // Alpha teacher against Alpha school yields the Alpha row …
        var alpha = await service.GetTeacherDashboardAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            CancellationToken.None);
        Assert.Single(alpha!.AssignedClasses);
        Assert.Equal(TeacherDashboardHarness.ClassAlpha, alpha.AssignedClasses[0].ClassGroupId);

        // … and lookup under the Beta tenant is null (teacher unknown).
        var betaLookup = await service.GetTeacherDashboardAsync(
            TeacherDashboardHarness.TenantBeta,
            TeacherDashboardHarness.SchoolBeta,
            TeacherDashboardHarness.TeacherMathAlpha,
            CancellationToken.None);
        Assert.Null(betaLookup);
    }
}
