using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Exams;

/// <summary>
/// T120 (US6) — Integration test for exam event emission into the
/// Phase 4 mastery pipeline.
///
/// Grading a submission MUST enqueue exactly one <c>exam_answered</c>
/// session event — the Phase 4 <c>ProgressIngestionWorker</c> consumes
/// those rows the same way it consumes quiz/mock-test events, so the
/// mastery → streak → badge pipeline does not need modification. The
/// test uses <see cref="RecordingSessionEventOutbox"/> to observe the
/// enqueue without wiring the Phase 3 dispatcher.
/// </summary>
public class ExamMasteryIntegrationTests
{
    [Fact]
    public async Task Grading_Emits_Exam_Answered_Session_Event()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var exam = await CreateExamAsync(harness);
        var studentId = harness.AlphaStudents[0];
        var questions = await harness.Questions().ListForExamAsync(ExamHarness.TenantAlpha, exam.ExamId, CancellationToken.None);
        var submission = new ExamSubmission
        {
            ExamSubmissionId = Guid.NewGuid(),
            TenantId = ExamHarness.TenantAlpha,
            ExamId = exam.ExamId,
            StudentId = studentId,
            Answers = harness.BuildAnswersJson(questions
                .Select(q => (q.ExamQuestionId, (object)new { option_id = "A" }))
                .ToList()),
            MaxScore = questions.Sum(q => q.Points),
            GradingStatus = "submitted",
            StartedAt = DateTime.UtcNow,
            SubmittedAt = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid().ToString("D"),
        };
        var subs = harness.Submissions();
        await subs.AddAsync(submission);
        await subs.SaveChangesAsync();

        var outbox = new RecordingSessionEventOutbox();
        var grader = harness.BuildAutoGrader(outbox);
        var sessionId = Guid.NewGuid();
        await grader.GradeAsync(
            submission,
            questions,
            studentSessionId: sessionId,
            subjectId: ExamHarness.SubjectMath,
            topicId: null,
            planTierSnapshot: "school",
            ct: CancellationToken.None);

        Assert.Single(outbox.Captured);
        var captured = outbox.Captured[0];
        Assert.Equal(SessionEventKind.exam_answered, captured.Kind);
        Assert.Equal(ExamHarness.TenantAlpha, captured.TenantId);
        Assert.Equal(sessionId, captured.StudentSessionId);
        Assert.Equal("school", captured.PlanTierSnapshot);
        Assert.Equal(ExamHarness.SubjectMath, captured.Scope.SubjectId);
    }

    private static async Task<Exam> CreateExamAsync(ExamHarness harness)
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
            DurationMinutes: 15,
            ScheduledStart: DateTime.UtcNow,
            ScheduledEnd: DateTime.UtcNow.AddHours(1),
            Questions: new[]
            {
                ExamHarness.MultipleChoiceQuestion("A", 2m),
                ExamHarness.MultipleChoiceQuestion("A", 2m),
            },
            ClassGroupIds: new[] { ExamHarness.ClassAlpha },
            CorrelationId: "corr-mastery"),
            CancellationToken.None);
        return result.Exam;
    }
}
