using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Exams.ExamResults;
using Muallimi.Api.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Exams.ExamAdministration;

/// <summary>
/// T129 (US6) — student exam view + submission endpoints.
///
/// Routes:
///   • GET  /api/student/exams                  — list exams for the
///       student's active class enrolments
///   • GET  /api/student/exams/{examId}         — exam questions during the
///       open window (returns options without the correct flag)
///   • POST /api/student/exams/{examId}/submit  — submit answers; auto-grade
///       if objective-only
///
/// The player relies on <see cref="ClassEnrolment"/> to answer "which
/// exams does this student see" and refuses access outside the open
/// window. Submissions are idempotent: the first call grades and writes;
/// subsequent calls return the existing submission row without
/// re-grading.
/// </summary>
public static class ExamPlayerEndpoints
{
    public const string StudentHeaderName = "X-Student-Profile-Id";
    public const string SessionHeaderName = "X-Session-Id";
    public const string ListRoute = "/api/student/exams";
    public const string DetailRoute = "/api/student/exams/{examId:guid}";
    public const string SubmitRoute = "/api/student/exams/{examId:guid}/submit";

    public sealed record SubmitAnswerPayload(Guid exam_question_id, JsonElement answer);

    public sealed record SubmitRequest(List<SubmitAnswerPayload> answers);

    public static IEndpointRouteBuilder MapExamPlayer(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ListRoute, HandleListAsync).WithName("ListStudentExams").WithTags("Exams");
        routes.MapGet(DetailRoute, HandleDetailAsync).WithName("GetStudentExam").WithTags("Exams");
        routes.MapPost(SubmitRoute, HandleSubmitAsync).WithName("SubmitStudentExam").WithTags("Exams");
        return routes;
    }

    public static async Task<IResult> HandleListAsync(
        HttpContext http,
        MuallimiDbContext db,
        IExamAssignmentRepository assignments,
        IExamSubmissionRepository submissions,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetStudentId(http, out var studentId))
        {
            return Results.Unauthorized();
        }

        var classIds = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && e.StudentId == studentId
                && e.Status == "active"
                && e.UnenrolledAt == null)
            .Select(e => e.ClassGroupId)
            .Distinct()
            .ToListAsync(ct);
        if (classIds.Count == 0)
        {
            return Results.Ok(new { exams = Array.Empty<object>() });
        }

        var examIds = await db.ExamAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && classIds.Contains(a.ClassGroupId))
            .Select(a => a.ExamId)
            .Distinct()
            .ToListAsync(ct);
        if (examIds.Count == 0)
        {
            return Results.Ok(new { exams = Array.Empty<object>() });
        }

        var exams = await db.Exams
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && examIds.Contains(e.ExamId))
            .OrderByDescending(e => e.ScheduledStart ?? e.CreatedAt)
            .ToListAsync(ct);

        var subs = await db.ExamSubmissions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.StudentId == studentId && examIds.Contains(s.ExamId))
            .ToListAsync(ct);
        var submittedSet = subs.Select(s => s.ExamId).ToHashSet();

        return Results.Ok(new
        {
            exams = exams.Where(e => e.Status != ExamStates.Draft).Select(e => new
            {
                exam_id = e.ExamId,
                title_ar = e.TitleAr,
                title_en = e.TitleEn,
                subject_id = e.SubjectId,
                status = e.Status,
                scheduled_start = e.ScheduledStart,
                scheduled_end = e.ScheduledEnd,
                duration_minutes = e.DurationMinutes,
                submitted = submittedSet.Contains(e.ExamId),
            }),
        });
    }

    public static async Task<IResult> HandleDetailAsync(
        Guid examId,
        HttpContext http,
        MuallimiDbContext db,
        IExamRepository exams,
        IExamQuestionRepository questions,
        IExamSubmissionRepository submissions,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetStudentId(http, out var studentId))
        {
            return Results.Unauthorized();
        }

        var exam = await db.Exams
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.ExamId == examId, ct);
        if (exam is null) return Results.NotFound(new { error = "exam_not_found" });

        var studentEligible = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Join(db.ExamAssignments.IgnoreQueryFilters().AsNoTracking(),
                ce => ce.ClassGroupId,
                ea => ea.ClassGroupId,
                (ce, ea) => new { ce, ea })
            .AnyAsync(x =>
                x.ce.TenantId == tenantId
                && x.ce.StudentId == studentId
                && x.ce.Status == "active"
                && x.ce.UnenrolledAt == null
                && x.ea.ExamId == examId,
                ct);
        if (!studentEligible) return Results.Forbid();

        if (exam.Status != ExamStates.Open)
        {
            return Results.Conflict(new { error = "exam_not_open", status = exam.Status });
        }

        var qList = await questions.ListForExamAsync(tenantId, examId, ct);
        var existing = await submissions.GetForStudentAsync(tenantId, examId, studentId, ct);

        return Results.Ok(new
        {
            exam_id = exam.ExamId,
            title_ar = exam.TitleAr,
            title_en = exam.TitleEn,
            duration_minutes = exam.DurationMinutes,
            scheduled_start = exam.ScheduledStart,
            scheduled_end = exam.ScheduledEnd,
            submitted = existing is not null,
            questions = qList.Select(q => new
            {
                exam_question_id = q.ExamQuestionId,
                question_text_ar = q.QuestionTextAr,
                question_text_en = q.QuestionTextEn,
                question_type = q.QuestionType,
                options = SanitiseOptions(q.Options),
                display_order = q.DisplayOrder,
                points = q.Points,
            }),
        });
    }

    public static async Task<IResult> HandleSubmitAsync(
        Guid examId,
        HttpContext http,
        SubmitRequest body,
        MuallimiDbContext db,
        IExamRepository exams,
        IExamQuestionRepository questions,
        IExamSubmissionRepository submissions,
        IExamAutoGrader grader,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetStudentId(http, out var studentId))
        {
            return Results.Unauthorized();
        }

        var exam = await db.Exams
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.ExamId == examId, ct);
        if (exam is null) return Results.NotFound(new { error = "exam_not_found" });
        if (exam.Status != ExamStates.Open)
        {
            return Results.Conflict(new { error = "exam_not_open", status = exam.Status });
        }

        var existing = await submissions.GetForStudentAsync(tenantId, examId, studentId, ct);
        if (existing is not null)
        {
            return Results.Ok(new
            {
                exam_submission_id = existing.ExamSubmissionId,
                grading_status = existing.GradingStatus,
                score = existing.Score ?? 0m,
                max_score = existing.MaxScore,
                idempotent = true,
            });
        }

        var qList = await questions.ListForExamAsync(tenantId, examId, ct);
        if (qList.Count == 0)
        {
            return Results.Conflict(new { error = "exam_has_no_questions" });
        }

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var sessionId = Guid.TryParse(http.Request.Headers[SessionHeaderName].ToString(), out var sid)
            ? sid
            : Guid.NewGuid();

        var now = DateTime.UtcNow;
        var submission = new ExamSubmission
        {
            ExamSubmissionId = Guid.NewGuid(),
            TenantId = tenantId,
            ExamId = examId,
            StudentId = studentId,
            Answers = SerialiseAnswers(body?.answers ?? new List<SubmitAnswerPayload>()),
            MaxScore = qList.Sum(q => q.Points),
            GradingStatus = "submitted",
            StartedAt = now,
            SubmittedAt = now,
            CorrelationId = correlationId,
        };

        await submissions.AddAsync(submission, ct);
        await submissions.SaveChangesAsync(ct);

        var result = await grader.GradeAsync(
            submission,
            qList,
            sessionId,
            exam.SubjectId,
            topicId: null,
            planTierSnapshot: "school",
            ct);

        return Results.Ok(new
        {
            exam_submission_id = submission.ExamSubmissionId,
            grading_status = submission.GradingStatus,
            score = result.Score,
            max_score = result.MaxScore,
        });
    }

    private static bool TryGetStudentId(HttpContext http, out Guid studentId)
        => Guid.TryParse(http.Request.Headers[StudentHeaderName].ToString(), out studentId);

    private static object? SanitiseOptions(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var options = new List<object>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                options.Add(new
                {
                    option_id = item.TryGetProperty("option_id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                        ? idProp.GetString()
                        : null,
                    label_ar = item.TryGetProperty("label_ar", out var arProp) && arProp.ValueKind == JsonValueKind.String
                        ? arProp.GetString()
                        : string.Empty,
                    label_en = item.TryGetProperty("label_en", out var enProp) && enProp.ValueKind == JsonValueKind.String
                        ? enProp.GetString()
                        : string.Empty,
                });
            }
            return options;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerialiseAnswers(List<SubmitAnswerPayload> payload)
    {
        var items = new List<object>();
        foreach (var a in payload)
        {
            items.Add(new
            {
                exam_question_id = a.exam_question_id,
                answer = a.answer.ValueKind == JsonValueKind.Undefined ? null : (object)a.answer,
            });
        }
        return JsonSerializer.Serialize(new { answers = items });
    }
}
