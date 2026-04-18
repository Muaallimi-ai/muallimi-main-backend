using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Exams;

/// <summary>
/// T116 (US6) — Contract test for exam CRUD and state machine
/// transitions.
///
/// Pins the route constants from the exam lifecycle contract and verifies
/// <see cref="ExamStateMachine"/> enforces the exact progression the
/// contract fixes: draft → scheduled → open → closed → graded →
/// published. Illegal transitions raise a deterministic error so the
/// endpoint layer can surface a consistent 409.
/// </summary>
public class ExamLifecycleTests
{
    [Fact]
    public void Route_Constants_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/exams", ExamEndpoints.CreateSchoolAdminRoute);
        Assert.Equal("/api/teacher/exams", ExamEndpoints.CreateTeacherRoute);
        Assert.Equal("/api/school-admin/exams/{examId:guid}/schedule", ExamEndpoints.ScheduleRoute);
        Assert.Equal("/api/school-admin/exams/{examId:guid}/open", ExamEndpoints.OpenRoute);
        Assert.Equal("/api/school-admin/exams/{examId:guid}/close", ExamEndpoints.CloseRoute);
    }

    [Theory]
    [InlineData("draft", "scheduled")]
    [InlineData("scheduled", "open")]
    [InlineData("scheduled", "closed")]
    [InlineData("open", "closed")]
    [InlineData("closed", "graded")]
    [InlineData("graded", "published")]
    public void State_Machine_Allows_Contracted_Transitions(string from, string to)
    {
        var next = ExamStateMachine.Transition(from, to);
        Assert.Equal(to, next);
    }

    [Theory]
    [InlineData("draft", "open")]
    [InlineData("draft", "published")]
    [InlineData("open", "scheduled")]
    [InlineData("closed", "open")]
    [InlineData("published", "graded")]
    public void State_Machine_Rejects_Invalid_Transitions(string from, string to)
    {
        Assert.Throws<InvalidExamStateTransitionException>(
            () => ExamStateMachine.Transition(from, to));
    }

    [Fact]
    public void Exam_Cannot_Open_Without_Questions()
    {
        Assert.False(ExamStateMachine.CanOpen("scheduled", questionCount: 0));
        Assert.True(ExamStateMachine.CanOpen("scheduled", questionCount: 3));
        Assert.False(ExamStateMachine.CanOpen("draft", questionCount: 3));
    }

    [Fact]
    public async Task Create_Exam_Persists_Draft_With_Questions_And_Assignments()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var service = harness.BuildCreationService();
        var result = await service.CreateAsync(new ExamCreationInput(
            TenantId: ExamHarness.TenantAlpha,
            SchoolTenantId: ExamHarness.SchoolAlpha,
            CreatedByTeacherId: ExamHarness.TeacherAlpha,
            CreatedByAdminId: null,
            TitleAr: "امتحان الرياضيات",
            TitleEn: "Math Exam",
            SubjectId: ExamHarness.SubjectMath,
            Grade: 7,
            TopicBindings: new List<string>(),
            DurationMinutes: 30,
            ScheduledStart: DateTime.UtcNow.AddDays(1),
            ScheduledEnd: DateTime.UtcNow.AddDays(1).AddHours(1),
            Questions: new[]
            {
                ExamHarness.MultipleChoiceQuestion("A", 2m),
                ExamHarness.TrueFalseQuestion(true, 1m),
            },
            ClassGroupIds: new[] { ExamHarness.ClassAlpha },
            CorrelationId: "corr-us6-lifecycle"),
            CancellationToken.None);

        Assert.Equal("draft", result.Exam.Status);
        Assert.Equal(3m, result.Exam.TotalPoints);
        Assert.Equal(2, result.Questions.Count);
        Assert.Single(result.Assignments);
        Assert.Equal(ExamHarness.ClassAlpha, result.Assignments[0].ClassGroupId);
    }
}
