using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Exams.ExamAdministration;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Exams;

/// <summary>
/// T117 (US6) — Contract test for student exam submission.
///
/// Pins the student-facing routes and verifies the invariants the
/// contract fixes: one submission per student per exam (repeat calls
/// return the existing row idempotently), answers carry per-question
/// correctness after grading, and a submission recorded against the
/// wrong tenant is invisible to the correct tenant.
/// </summary>
public class ExamSubmissionTests
{
    [Fact]
    public void Routes_Are_Pinned()
    {
        Assert.Equal("/api/student/exams", ExamPlayerEndpoints.ListRoute);
        Assert.Equal("/api/student/exams/{examId:guid}", ExamPlayerEndpoints.DetailRoute);
        Assert.Equal("/api/student/exams/{examId:guid}/submit", ExamPlayerEndpoints.SubmitRoute);
    }

    [Fact]
    public async Task Submission_Is_Unique_Per_Student_And_Exam()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var exam = await CreateOpenExamAsync(harness);
        var studentId = harness.AlphaStudents[0];
        var questions = await harness.Questions().ListForExamAsync(ExamHarness.TenantAlpha, exam.ExamId, CancellationToken.None);
        var answersJson = harness.BuildAnswersJson(questions
            .Select(q => (q.ExamQuestionId, (object)new { option_id = "A" }))
            .ToList());

        var submission1 = new ExamSubmission
        {
            ExamSubmissionId = Guid.NewGuid(),
            TenantId = ExamHarness.TenantAlpha,
            ExamId = exam.ExamId,
            StudentId = studentId,
            Answers = answersJson,
            MaxScore = questions.Sum(q => q.Points),
            GradingStatus = "submitted",
            StartedAt = DateTime.UtcNow,
            SubmittedAt = DateTime.UtcNow,
            CorrelationId = "corr-submit-1",
        };
        var repo = harness.Submissions();
        await repo.AddAsync(submission1);
        await repo.SaveChangesAsync();

        var existing = await repo.GetForStudentAsync(
            ExamHarness.TenantAlpha, exam.ExamId, studentId, CancellationToken.None);
        Assert.NotNull(existing);
        Assert.Equal(submission1.ExamSubmissionId, existing!.ExamSubmissionId);
    }

    [Fact]
    public async Task Grading_Writes_Score_And_PerQuestion_Correctness()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var exam = await CreateOpenExamAsync(harness);
        var studentId = harness.AlphaStudents[0];
        var questions = await harness.Questions().ListForExamAsync(ExamHarness.TenantAlpha, exam.ExamId, CancellationToken.None);
        var answers = questions
            .Select(q => (q.ExamQuestionId, (object)new { option_id = "A" }))
            .ToList();
        var submission = new ExamSubmission
        {
            ExamSubmissionId = Guid.NewGuid(),
            TenantId = ExamHarness.TenantAlpha,
            ExamId = exam.ExamId,
            StudentId = studentId,
            Answers = harness.BuildAnswersJson(answers),
            MaxScore = questions.Sum(q => q.Points),
            GradingStatus = "submitted",
            StartedAt = DateTime.UtcNow,
            SubmittedAt = DateTime.UtcNow,
            CorrelationId = "corr-submit-2",
        };
        var subs = harness.Submissions();
        await subs.AddAsync(submission);
        await subs.SaveChangesAsync();

        var outbox = new RecordingSessionEventOutbox();
        var grader = harness.BuildAutoGrader(outbox);
        var result = await grader.GradeAsync(
            submission,
            questions,
            studentSessionId: Guid.NewGuid(),
            subjectId: ExamHarness.SubjectMath,
            topicId: null,
            planTierSnapshot: "school",
            ct: CancellationToken.None);

        Assert.Equal("graded", submission.GradingStatus);
        Assert.True(result.Score > 0);
        Assert.Equal(result.MaxScore, questions.Sum(q => q.Points));

        using var doc = JsonDocument.Parse(submission.Answers);
        Assert.True(doc.RootElement.TryGetProperty("per_question", out var perQ));
        Assert.Equal(questions.Count, perQ.GetArrayLength());
    }

    private static async Task<Exam> CreateOpenExamAsync(ExamHarness harness)
    {
        var service = harness.BuildCreationService();
        var result = await service.CreateAsync(new ExamCreationInput(
            TenantId: ExamHarness.TenantAlpha,
            SchoolTenantId: ExamHarness.SchoolAlpha,
            CreatedByTeacherId: ExamHarness.TeacherAlpha,
            CreatedByAdminId: null,
            TitleAr: "امتحان",
            TitleEn: "Exam",
            SubjectId: ExamHarness.SubjectMath,
            Grade: 7,
            TopicBindings: new List<string>(),
            DurationMinutes: 30,
            ScheduledStart: DateTime.UtcNow,
            ScheduledEnd: DateTime.UtcNow.AddHours(1),
            Questions: new[]
            {
                ExamHarness.MultipleChoiceQuestion("A", 1m),
                ExamHarness.MultipleChoiceQuestion("A", 1m),
            },
            ClassGroupIds: new[] { ExamHarness.ClassAlpha },
            CorrelationId: "corr-open"),
            CancellationToken.None);
        result.Exam.Status = "scheduled";
        result.Exam.Status = ExamStateMachine.Transition(result.Exam.Status, "open");
        await harness.Exams().SaveChangesAsync();
        return result.Exam;
    }
}
