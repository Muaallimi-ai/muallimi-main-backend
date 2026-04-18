using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Exams;

/// <summary>
/// T119 (US6) — Integration test for auto-grading correctness across the
/// three objective question types required by the contract.
///
/// Each scenario creates a minimal exam, drives a single submission
/// through the grader with a known-correct and known-wrong mix, and
/// asserts the awarded score matches the configured points. A mixed-
/// answer scenario locks in the "partial credit is per question, not
/// proportional" rubric.
/// </summary>
public class AutoGradingTests
{
    [Fact]
    public async Task Multiple_Choice_Grades_Correct_And_Wrong_Answers()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var result = await harness.BuildCreationService().CreateAsync(new ExamCreationInput(
            TenantId: ExamHarness.TenantAlpha,
            SchoolTenantId: ExamHarness.SchoolAlpha,
            CreatedByTeacherId: ExamHarness.TeacherAlpha,
            CreatedByAdminId: null,
            TitleAr: "اختياري",
            TitleEn: "MCQ",
            SubjectId: ExamHarness.SubjectMath,
            Grade: 7,
            TopicBindings: new List<string>(),
            DurationMinutes: 15,
            ScheduledStart: DateTime.UtcNow,
            ScheduledEnd: DateTime.UtcNow.AddHours(1),
            Questions: new[]
            {
                ExamHarness.MultipleChoiceQuestion("A", 2m),
                ExamHarness.MultipleChoiceQuestion("B", 2m),
            },
            ClassGroupIds: new[] { ExamHarness.ClassAlpha },
            CorrelationId: "corr-auto-mcq"),
            CancellationToken.None);

        var questions = result.Questions;
        var submission = await SubmitAsync(harness, result.Exam, harness.AlphaStudents[0], new[]
        {
            (questions[0].ExamQuestionId, (object)new { option_id = "A" }), // correct
            (questions[1].ExamQuestionId, (object)new { option_id = "C" }), // wrong
        });
        var grader = harness.BuildAutoGrader(new RecordingSessionEventOutbox());
        var outcome = await grader.GradeAsync(
            submission, questions, Guid.NewGuid(), ExamHarness.SubjectMath, null, "school", CancellationToken.None);

        Assert.Equal(2m, outcome.Score);
        Assert.Equal(4m, outcome.MaxScore);
        Assert.True(outcome.PerQuestion[0].IsCorrect);
        Assert.False(outcome.PerQuestion[1].IsCorrect);
    }

    [Fact]
    public async Task TrueFalse_Grades_Boolean_Comparison()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var result = await harness.BuildCreationService().CreateAsync(new ExamCreationInput(
            TenantId: ExamHarness.TenantAlpha,
            SchoolTenantId: ExamHarness.SchoolAlpha,
            CreatedByTeacherId: ExamHarness.TeacherAlpha,
            CreatedByAdminId: null,
            TitleAr: "صح/خطأ",
            TitleEn: "TF",
            SubjectId: ExamHarness.SubjectMath,
            Grade: 7,
            TopicBindings: new List<string>(),
            DurationMinutes: 15,
            ScheduledStart: DateTime.UtcNow,
            ScheduledEnd: DateTime.UtcNow.AddHours(1),
            Questions: new[]
            {
                ExamHarness.TrueFalseQuestion(true, 1m),
                ExamHarness.TrueFalseQuestion(false, 1m),
            },
            ClassGroupIds: new[] { ExamHarness.ClassAlpha },
            CorrelationId: "corr-auto-tf"),
            CancellationToken.None);

        var questions = result.Questions;
        var submission = await SubmitAsync(harness, result.Exam, harness.AlphaStudents[0], new[]
        {
            (questions[0].ExamQuestionId, (object)new { value = true }),   // correct
            (questions[1].ExamQuestionId, (object)new { value = true }),   // wrong
        });
        var grader = harness.BuildAutoGrader(new RecordingSessionEventOutbox());
        var outcome = await grader.GradeAsync(
            submission, questions, Guid.NewGuid(), ExamHarness.SubjectMath, null, "school", CancellationToken.None);

        Assert.Equal(1m, outcome.Score);
        Assert.Equal(2m, outcome.MaxScore);
    }

    [Fact]
    public async Task Fill_In_Blank_Normalises_Case_And_Whitespace()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var result = await harness.BuildCreationService().CreateAsync(new ExamCreationInput(
            TenantId: ExamHarness.TenantAlpha,
            SchoolTenantId: ExamHarness.SchoolAlpha,
            CreatedByTeacherId: ExamHarness.TeacherAlpha,
            CreatedByAdminId: null,
            TitleAr: "املأ",
            TitleEn: "Fill",
            SubjectId: ExamHarness.SubjectMath,
            Grade: 7,
            TopicBindings: new List<string>(),
            DurationMinutes: 15,
            ScheduledStart: DateTime.UtcNow,
            ScheduledEnd: DateTime.UtcNow.AddHours(1),
            Questions: new[] { ExamHarness.FillInBlankQuestion("Paris", 3m) },
            ClassGroupIds: new[] { ExamHarness.ClassAlpha },
            CorrelationId: "corr-auto-fib"),
            CancellationToken.None);

        var questions = result.Questions;
        var submission = await SubmitAsync(harness, result.Exam, harness.AlphaStudents[0], new[]
        {
            (questions[0].ExamQuestionId, (object)new { value = "  paris " }), // correct (normalised)
        });
        var grader = harness.BuildAutoGrader(new RecordingSessionEventOutbox());
        var outcome = await grader.GradeAsync(
            submission, questions, Guid.NewGuid(), ExamHarness.SubjectMath, null, "school", CancellationToken.None);

        Assert.Equal(3m, outcome.Score);
        Assert.True(outcome.PerQuestion[0].IsCorrect);
    }

    private static async Task<ExamSubmission> SubmitAsync(
        ExamHarness harness,
        Exam exam,
        Guid studentId,
        IReadOnlyList<(Guid QuestionId, object Answer)> answers)
    {
        var submission = new ExamSubmission
        {
            ExamSubmissionId = Guid.NewGuid(),
            TenantId = exam.TenantId,
            ExamId = exam.ExamId,
            StudentId = studentId,
            Answers = harness.BuildAnswersJson(answers),
            MaxScore = exam.TotalPoints,
            GradingStatus = "submitted",
            StartedAt = DateTime.UtcNow,
            SubmittedAt = DateTime.UtcNow,
            CorrelationId = "corr-auto-grade",
        };
        var repo = harness.Submissions();
        await repo.AddAsync(submission);
        await repo.SaveChangesAsync();
        return submission;
    }
}
