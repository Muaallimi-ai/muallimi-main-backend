using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.TeacherDashboard;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.TeacherDashboard;

/// <summary>
/// T104 (US5) — Contract test for GET /api/teacher/dashboard/student/{id}.
///
/// Verifies the student view projects only mastery / focus areas for the
/// subjects the teacher is actively assigned to teach the student's class,
/// surfaces badges and streak length, and returns the intervention prompt
/// when an open at-risk flag exists.
/// </summary>
public class StudentDetailTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal(
            "/api/teacher/dashboard/student/{studentId:guid}",
            TeacherDetailEndpoints.StudentRoute);
    }

    [Fact]
    public async Task Student_Detail_Scopes_Mastery_To_Assigned_Subjects()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var studentId = harness.AlphaClassAStudents[1]; // mid student carries focus area

        var response = await service.GetStudentDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            studentId,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(studentId, response!.StudentId);
        // Only math mastery — the Arabic row is filtered even though a row exists.
        Assert.Single(response.Mastery);
        Assert.Equal(TeacherDashboardHarness.SubjectMath, response.Mastery[0].SubjectId);
        Assert.Equal("Mathematics", response.Mastery[0].SubjectNameEn);

        Assert.Single(response.FocusAreas);
        Assert.Contains("division", response.FocusAreas[0].RationaleEn, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/lessons/", response.FocusAreas[0].DeepLink);

        Assert.False(response.AtRisk);
        Assert.Null(response.InterventionPrompt);
        Assert.Equal(3, response.StreakLength);
    }

    [Fact]
    public async Task Student_Detail_Returns_Intervention_Prompt_For_At_Risk_Student()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var lowStudent = harness.AlphaClassAStudents[0];

        var response = await service.GetStudentDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            lowStudent,
            CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response!.AtRisk);
        Assert.NotNull(response.InterventionPrompt);
        Assert.Equal("study_mode", response.InterventionPrompt!.NextStepPhase3Mode);
        Assert.False(string.IsNullOrWhiteSpace(response.InterventionPrompt.BodyAr));
        Assert.False(string.IsNullOrWhiteSpace(response.InterventionPrompt.BodyEn));
    }

    [Fact]
    public async Task Student_Detail_Returns_Null_When_Student_Not_In_Assigned_Class()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TeacherDashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var service = harness.BuildService();
        var unrelatedStudent = harness.AlphaClassBStudents[0];

        var response = await service.GetStudentDetailAsync(
            TeacherDashboardHarness.TenantAlpha,
            TeacherDashboardHarness.SchoolAlpha,
            TeacherDashboardHarness.TeacherMathAlpha,
            unrelatedStudent,
            CancellationToken.None);

        Assert.Null(response);
    }
}
