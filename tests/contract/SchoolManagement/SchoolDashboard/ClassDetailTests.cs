using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.SchoolDashboard;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolDashboard;

/// <summary>
/// T085 (US4) — Contract test for GET /api/school-admin/dashboard/class/{id}.
///
/// Pins the route constant and verifies the class-detail response shape:
///   • class metadata + student_count;
///   • per-student mastery_summary buckets by subject_id;
///   • at_risk boolean flips when the student has an open AtRiskFlag;
///   • focus_areas_count, streak_length, badges_earned surfaced from
///     Phase 4 tables;
///   • class_mastery rolls per-student mastery by subject_id.
/// </summary>
public class ClassDetailTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/school-admin/dashboard/class/{classGroupId:guid}", ClassDetailEndpoints.ClassDetailRoute);
    }

    [Fact]
    public async Task Class_Detail_Projects_Per_Student_Mastery_And_Flags()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetClassDetailAsync(
            DashboardHarness.TenantAlpha,
            DashboardHarness.SchoolAlpha,
            DashboardHarness.ClassAlpha,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(DashboardHarness.ClassAlpha, response!.ClassGroupId);
        Assert.Equal(3, response.StudentCount);
        Assert.Equal(3, response.Students.Count);

        // Each student has at least one mastery row for the seeded subject.
        foreach (var student in response.Students)
        {
            Assert.Contains(student.MasterySummary, m => m.SubjectId == DashboardHarness.SubjectMath.ToString());
        }

        // At-risk boolean isolated to the lowest scorer (harness seeds 1
        // open flag for the first student).
        Assert.Single(response.Students.Where(s => s.AtRisk));
        Assert.Equal(harness.AlphaStudentIds[0], response.Students.First(s => s.AtRisk).StudentId);

        // Focus area lives on the mid student (idx 1).
        var midStudent = response.Students.First(s => s.StudentId == harness.AlphaStudentIds[1]);
        Assert.Equal(1, midStudent.FocusAreasCount);

        // Top student (idx 2) has one badge.
        var topStudent = response.Students.First(s => s.StudentId == harness.AlphaStudentIds[^1]);
        Assert.True(topStudent.BadgesEarned >= 1);

        // Class mastery rolls up across students.
        Assert.Single(response.ClassMastery);
        Assert.Equal(DashboardHarness.SubjectMath.ToString(), response.ClassMastery[0].SubjectId);
    }

    [Fact]
    public async Task Class_Detail_Returns_Null_For_Unknown_Class()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var response = await service.GetClassDetailAsync(
            DashboardHarness.TenantAlpha,
            DashboardHarness.SchoolAlpha,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Null(response);
    }
}
