using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Exams.ExamAdministration;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.SchoolManagement;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Exams.ExamResults;

/// <summary>
/// T130 / T132 (US6) — teacher exam results + publish endpoints.
///
/// Routes:
///   • GET  /api/teacher/exams/{examId}/results  — roll-up of submissions
///       per class (average, distribution, per-question correct rate)
///   • POST /api/teacher/exams/{examId}/publish  — transition graded→published
///       and enqueue the Phase 5 <c>exam_published</c> downstream event so
///       operator dashboards and Phase 6 consumers can fan out.
/// </summary>
public static class ExamResultEndpoints
{
    public const string ResultsRoute = "/api/teacher/exams/{examId:guid}/results";
    public const string PublishRoute = "/api/teacher/exams/{examId:guid}/publish";

    public static IEndpointRouteBuilder MapExamResults(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ResultsRoute, HandleResultsAsync).WithName("GetExamResults").WithTags("Exams");
        routes.MapPost(PublishRoute, HandlePublishAsync).WithName("PublishExamResults").WithTags("Exams");
        return routes;
    }

    public static async Task<IResult> HandleResultsAsync(
        Guid examId,
        HttpContext http,
        MuallimiDbContext db,
        IExamRepository exams,
        IExamQuestionRepository questions,
        IExamAssignmentRepository assignments,
        IExamSubmissionRepository submissions,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId, out var teacherId, out var adminId))
        {
            return Results.Unauthorized();
        }

        var exam = await exams.GetByIdAsync(tenantId, schoolTenantId, examId, ct);
        if (exam is null) return Results.NotFound(new { error = "exam_not_found" });
        if (teacherId is not null && exam.CreatedByTeacherId != teacherId)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var qList = await questions.ListForExamAsync(tenantId, examId, ct);
        var assignmentRows = await assignments.ListForExamAsync(tenantId, examId, ct);
        var subs = await submissions.ListForExamAsync(tenantId, examId, ct);

        var classGroupIds = assignmentRows.Select(a => a.ClassGroupId).ToHashSet();
        var classes = await db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && classGroupIds.Contains(c.ClassGroupId))
            .ToListAsync(ct);

        var enrolments = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && classGroupIds.Contains(e.ClassGroupId)
                && e.Status == "active"
                && e.UnenrolledAt == null)
            .ToListAsync(ct);

        var studentIds = subs.Select(s => s.StudentId).ToHashSet();
        var students = await db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.DisplayName })
            .ToListAsync(ct);
        var studentMap = students.ToDictionary(s => s.Id, s => s.DisplayName);
        var subsByStudent = subs.ToDictionary(s => s.StudentId, s => s);

        var classResults = new List<object>();
        foreach (var c in classes)
        {
            var classStudents = enrolments
                .Where(e => e.ClassGroupId == c.ClassGroupId)
                .Select(e => e.StudentId)
                .ToList();
            var classSubs = classStudents
                .Where(id => subsByStudent.ContainsKey(id))
                .Select(id => subsByStudent[id])
                .ToList();

            var scores = classSubs.Select(s => s.Score ?? 0m).ToList();
            classResults.Add(new
            {
                class_group_id = c.ClassGroupId,
                display_name_ar = c.DisplayNameAr,
                display_name_en = c.DisplayNameEn,
                submission_count = classSubs.Count,
                expected_count = classStudents.Count,
                average_score = scores.Count == 0 ? 0m : Math.Round(scores.Average(), 4),
                max_score = exam.TotalPoints,
                score_distribution = BuildDistribution(scores, exam.TotalPoints),
                submissions = classSubs.Select(s => new
                {
                    student_id = s.StudentId,
                    display_name_ar = studentMap.TryGetValue(s.StudentId, out var n1) ? n1 : string.Empty,
                    display_name_en = studentMap.TryGetValue(s.StudentId, out var n2) ? n2 : string.Empty,
                    score = s.Score ?? 0m,
                    submitted_at = s.SubmittedAt,
                    grading_status = s.GradingStatus,
                }),
            });
        }

        var perQuestion = new List<object>();
        foreach (var q in qList)
        {
            var attempts = 0;
            var correct = 0;
            foreach (var s in subs)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(s.Answers) ? "{}" : s.Answers);
                if (!doc.RootElement.TryGetProperty("per_question", out var pq)
                    || pq.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var item in pq.EnumerateArray())
                {
                    if (!item.TryGetProperty("exam_question_id", out var id)) continue;
                    if (id.GetString() != q.ExamQuestionId.ToString("D")) continue;
                    attempts++;
                    if (item.TryGetProperty("is_correct", out var ic)
                        && ic.ValueKind == System.Text.Json.JsonValueKind.True)
                    {
                        correct++;
                    }
                }
            }
            perQuestion.Add(new
            {
                exam_question_id = q.ExamQuestionId,
                question_text_ar = q.QuestionTextAr,
                question_text_en = q.QuestionTextEn,
                correct_rate = attempts == 0 ? 0m : Math.Round((decimal)correct / attempts, 4),
                attempts,
            });
        }

        return Results.Ok(new
        {
            exam_id = exam.ExamId,
            title_ar = exam.TitleAr,
            title_en = exam.TitleEn,
            status = exam.Status,
            class_results = classResults,
            question_analysis = perQuestion,
        });
    }

    public static async Task<IResult> HandlePublishAsync(
        Guid examId,
        HttpContext http,
        IExamRepository exams,
        IExamSubmissionRepository submissions,
        IPhase5DownstreamEventOutbox outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId, out var teacherId, out var adminId))
        {
            return Results.Unauthorized();
        }

        var exam = await exams.GetByIdAsync(tenantId, schoolTenantId, examId, ct);
        if (exam is null) return Results.NotFound(new { error = "exam_not_found" });
        if (teacherId is not null && exam.CreatedByTeacherId != teacherId)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (exam.Status == ExamStates.Closed)
        {
            try
            {
                exam.Status = ExamStateMachine.Transition(exam.Status, ExamStates.Graded);
            }
            catch (InvalidExamStateTransitionException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }

        try
        {
            exam.Status = ExamStateMachine.Transition(exam.Status, ExamStates.Published);
        }
        catch (InvalidExamStateTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }

        exam.UpdatedAt = DateTime.UtcNow;

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var subs = await submissions.ListForExamAsync(tenantId, examId, ct);
        await outbox.EnqueueAsync(
            Phase5DownstreamEventKind.exam_published,
            tenantId,
            schoolTenantId,
            new
            {
                exam_id = exam.ExamId,
                subject_id = exam.SubjectId,
                grade = exam.Grade,
                submission_count = subs.Count,
                published_by_teacher_id = teacherId,
                published_by_admin_id = adminId,
            },
            correlationId,
            occurredAt: DateTime.UtcNow,
            ct: ct);

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            exam_id = exam.ExamId,
            status = exam.Status,
            published_at = exam.UpdatedAt,
        });
    }

    private static bool TryResolveScope(
        HttpContext http,
        out Guid tenantId,
        out Guid schoolTenantId,
        out Guid? teacherId,
        out Guid? adminId)
    {
        teacherId = null;
        adminId = null;
        tenantId = Guid.Empty;
        schoolTenantId = Guid.Empty;
        if (!SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId))
        {
            return false;
        }
        if (SchoolManagementHeaders.TryGetTeacherId(http, out var t))
        {
            teacherId = t;
            return true;
        }
        if (SchoolManagementHeaders.TryGetSchoolAdminId(http, out var a))
        {
            adminId = a;
            return true;
        }
        return false;
    }

    private static object BuildDistribution(List<decimal> scores, decimal maxScore)
    {
        if (scores.Count == 0 || maxScore <= 0m)
        {
            return new { buckets = Array.Empty<object>() };
        }
        var ranges = new[] { 0m, 0.25m, 0.50m, 0.75m, 1.0m };
        var buckets = new List<object>();
        for (var i = 0; i < ranges.Length - 1; i++)
        {
            var lower = ranges[i] * maxScore;
            var upper = ranges[i + 1] * maxScore;
            var count = scores.Count(s => s >= lower && (i == ranges.Length - 2 ? s <= upper : s < upper));
            buckets.Add(new
            {
                lower = Math.Round(lower, 2),
                upper = Math.Round(upper, 2),
                count,
            });
        }
        return new { buckets };
    }
}
