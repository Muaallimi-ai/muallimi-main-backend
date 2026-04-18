using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Exams;

/// <summary>
/// T121 (US6) — Integration test for guardrail chain pass-through on
/// custom questions.
///
/// Confirms that a clean custom question writes a
/// <c>GuardrailDecisionTrail</c> row and gets a non-null trail id on
/// the persisted <see cref="ExamQuestion"/>, and that a custom
/// question containing banned content is rejected before any exam,
/// question, or assignment row is persisted — the whole unit of work
/// is aborted.
/// </summary>
public class CustomQuestionGuardrailTests
{
    [Fact]
    public async Task Clean_Custom_Question_Writes_Guardrail_Trail()
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
            TitleAr: "امتحان مخصص",
            TitleEn: "Custom Exam",
            SubjectId: ExamHarness.SubjectMath,
            Grade: 7,
            TopicBindings: new List<string>(),
            DurationMinutes: 30,
            ScheduledStart: DateTime.UtcNow,
            ScheduledEnd: DateTime.UtcNow.AddHours(1),
            Questions: new[]
            {
                ExamHarness.CustomQuestion(
                    ar: "ما ناتج جمع اثنين واثنين",
                    en: "What is two plus two"),
            },
            ClassGroupIds: new[] { ExamHarness.ClassAlpha },
            CorrelationId: "corr-custom-clean"),
            CancellationToken.None);

        Assert.Single(result.Questions);
        Assert.Equal("custom", result.Questions[0].QuestionSource);
        Assert.NotNull(result.Questions[0].GuardrailDecisionTrailId);

        var trailCount = await db.GuardrailDecisionTrails
            .IgnoreQueryFilters()
            .CountAsync(t => t.ArtefactKind == CustomQuestionGuardrailValidator.ArtefactKind);
        Assert.Equal(1, trailCount);
    }

    [Fact]
    public async Task Banned_Custom_Question_Is_Rejected_And_Nothing_Is_Persisted()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new ExamHarness(db);
        await harness.SeedAsync();

        var service = harness.BuildCreationService();

        var ex = await Assert.ThrowsAsync<CustomQuestionRejectedException>(() =>
            service.CreateAsync(new ExamCreationInput(
                TenantId: ExamHarness.TenantAlpha,
                SchoolTenantId: ExamHarness.SchoolAlpha,
                CreatedByTeacherId: ExamHarness.TeacherAlpha,
                CreatedByAdminId: null,
                TitleAr: "امتحان مرفوض",
                TitleEn: "Rejected Exam",
                SubjectId: ExamHarness.SubjectMath,
                Grade: 7,
                TopicBindings: new List<string>(),
                DurationMinutes: 30,
                ScheduledStart: DateTime.UtcNow,
                ScheduledEnd: DateTime.UtcNow.AddHours(1),
                Questions: new[]
                {
                    ExamHarness.CustomQuestion(
                        ar: "أنت غبي في الرياضيات",
                        en: "You are stupid at math"),
                },
                ClassGroupIds: new[] { ExamHarness.ClassAlpha },
                CorrelationId: "corr-custom-banned"),
                CancellationToken.None));

        Assert.NotEmpty(ex.Violations);

        // Nothing persisted — the service aborts SaveChanges before the
        // exam/question/assignment rows are committed.
        var examCount = await db.Exams.IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == ExamHarness.TenantAlpha);
        var questionCount = await db.ExamQuestions.IgnoreQueryFilters()
            .CountAsync(q => q.TenantId == ExamHarness.TenantAlpha);
        var assignmentCount = await db.ExamAssignments.IgnoreQueryFilters()
            .CountAsync(a => a.TenantId == ExamHarness.TenantAlpha);
        Assert.Equal(0, examCount);
        Assert.Equal(0, questionCount);
        Assert.Equal(0, assignmentCount);
    }
}
