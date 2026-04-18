using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.TeacherDashboard;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.TeacherDashboard;

/// <summary>
/// T103 (US5) — Contract test for
/// GET /api/teacher/dashboard/class/{id}/subject/{id}.
///
/// Pins the route constant and verifies:
///   • per-student mastery rows carry score, band, focus areas, at-risk
///     flag, streak length;
///   • focus_areas are filtered by the teacher's subject — the Arabic
///     focus-area row seeded by the harness does NOT appear in the Math
///     response;
///   • unassigned teacher gets null (endpoint maps to 403);
///   • wrong subject for the assigned class gets null.
/// </summary>
public class ClassSubjectDetailTests
{
    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal(
            "/api/teacher/dashboard/class/{classGroupId:guid}/subject/{subjectId:guid}",
            TeacherDetailEndpoints.ClassSubjectRoute);
    }

    [Fact]
    public async Task ClassSubject_Detail_Returns_Per_Student_Rows_Scoped_To_Subject()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetClassSubjectDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            TeacherDashboardHarness.ClassAlpha,
            TeacherDashboardHarness.SubjectMath,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(3, response!.Students.Count);

        // Lowest scorer at-risk, carries mastery, focus_areas empty.
        var low = response.Students.First(s => s.StudentId == harness.AlphaClassAStudents[0]);
        Assert.True(low.AtRisk);
        Assert.Equal(0, low.StreakLength);
        Assert.Equal(0.20m, low.MasteryScore);
        Assert.Equal("introduced", low.MasteryBand);
        Assert.Empty(low.FocusAreas);

        // Mid student has one math focus area — the Arabic one must be filtered.
        var mid = response.Students.First(s => s.StudentId == harness.AlphaClassAStudents[1]);
        Assert.Single(mid.FocusAreas);
        Assert.Contains("قسمة", mid.FocusAreas[0].RationaleAr);

        // Top student confident band.
        var top = response.Students.First(s => s.StudentId == harness.AlphaClassAStudents[^1]);
        Assert.Equal("confident", top.MasteryBand);
    }

    [Fact]
    public async Task ClassSubject_Detail_Returns_Null_For_Unassigned_Teacher()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetClassSubjectDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            TeacherDashboardHarness.ClassAlphaB, // teacher is NOT assigned to class B
            TeacherDashboardHarness.SubjectMath,
            CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task ClassSubject_Detail_Returns_Null_For_Unassigned_Subject()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetClassSubjectDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            TeacherDashboardHarness.ClassAlpha,
            TeacherDashboardHarness.SubjectArabic, // teacher teaches Math, not Arabic
            CancellationToken.None);

        Assert.Null(response);
    }
}
