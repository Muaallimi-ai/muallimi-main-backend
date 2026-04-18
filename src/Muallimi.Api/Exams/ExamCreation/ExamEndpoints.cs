using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.Exams.ExamCreation;

/// <summary>
/// T128 (US6) — exam CRUD + state transition endpoints.
///
/// Routes:
///   • POST /api/school-admin/exams             — school admin creates exam
///   • POST /api/teacher/exams                  — teacher creates exam
///   • GET  /api/school-admin/exams             — list exams for the school
///   • GET  /api/teacher/exams                  — list exams the teacher owns
///   • PUT  /api/school-admin/exams/{id}/schedule — transition draft→scheduled
///   • POST /api/school-admin/exams/{id}/open    — transition scheduled→open
///   • POST /api/school-admin/exams/{id}/close   — transition open→closed
///
/// All endpoints enforce tenant + school-tenant scope via
/// <see cref="SchoolManagementHeaders"/>. Teacher routes additionally
/// require <c>X-Teacher-Id</c> and reject requests targeting exams the
/// teacher did not create.
/// </summary>
public static class ExamEndpoints
{
    public const string CreateSchoolAdminRoute = "/api/school-admin/exams";
    public const string CreateTeacherRoute = "/api/teacher/exams";
    public const string ListSchoolAdminRoute = "/api/school-admin/exams";
    public const string ListTeacherRoute = "/api/teacher/exams";
    public const string ScheduleRoute = "/api/school-admin/exams/{examId:guid}/schedule";
    public const string OpenRoute = "/api/school-admin/exams/{examId:guid}/open";
    public const string CloseRoute = "/api/school-admin/exams/{examId:guid}/close";

    public sealed record ExamQuestionPayload(
        string question_source,
        Guid? phase1_content_id,
        string question_text_ar,
        string question_text_en,
        string question_type,
        JsonElement? options,
        JsonElement correct_answer,
        decimal points);

    public sealed record ExamCreateRequest(
        string title_ar,
        string title_en,
        Guid subject_id,
        int grade,
        List<string>? topic_bindings,
        int? duration_minutes,
        DateTime? scheduled_start,
        DateTime? scheduled_end,
        List<ExamQuestionPayload> questions,
        List<Guid> class_group_ids);

    public sealed record ScheduleRequest(DateTime scheduled_start, DateTime scheduled_end, int? duration_minutes);

    public static IEndpointRouteBuilder MapExamCreation(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(CreateSchoolAdminRoute, HandleCreateByAdminAsync).WithName("CreateExamByAdmin").WithTags("Exams");
        routes.MapPost(CreateTeacherRoute, HandleCreateByTeacherAsync).WithName("CreateExamByTeacher").WithTags("Exams");
        routes.MapGet(ListSchoolAdminRoute, HandleListBySchoolAsync).WithName("ListExamsBySchool").WithTags("Exams");
        routes.MapGet(ListTeacherRoute, HandleListByTeacherAsync).WithName("ListExamsByTeacher").WithTags("Exams");
        routes.MapPut(ScheduleRoute, HandleScheduleAsync).WithName("ScheduleExam").WithTags("Exams");
        routes.MapPost(OpenRoute, HandleOpenAsync).WithName("OpenExam").WithTags("Exams");
        routes.MapPost(CloseRoute, HandleCloseAsync).WithName("CloseExam").WithTags("Exams");
        return routes;
    }

    public static async Task<IResult> HandleCreateByAdminAsync(
        HttpContext http,
        ExamCreateRequest body,
        IExamCreationService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetSchoolAdminId(http, out var adminId))
        {
            return Results.Unauthorized();
        }
        return await CreateAsync(http, body, service, tenantId, schoolTenantId, createdByTeacherId: null, createdByAdminId: adminId, ct);
    }

    public static async Task<IResult> HandleCreateByTeacherAsync(
        HttpContext http,
        ExamCreateRequest body,
        IExamCreationService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetTeacherId(http, out var teacherId))
        {
            return Results.Unauthorized();
        }
        return await CreateAsync(http, body, service, tenantId, schoolTenantId, createdByTeacherId: teacherId, createdByAdminId: null, ct);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        ExamCreateRequest body,
        IExamCreationService service,
        Guid tenantId,
        Guid schoolTenantId,
        Guid? createdByTeacherId,
        Guid? createdByAdminId,
        CancellationToken ct)
    {
        if (body is null
            || string.IsNullOrWhiteSpace(body.title_ar)
            || string.IsNullOrWhiteSpace(body.title_en)
            || body.questions is null
            || body.questions.Count == 0
            || body.class_group_ids is null
            || body.class_group_ids.Count == 0)
        {
            return Results.BadRequest(new { error = "invalid_exam_payload" });
        }

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var input = new ExamCreationInput(
            TenantId: tenantId,
            SchoolTenantId: schoolTenantId,
            CreatedByTeacherId: createdByTeacherId,
            CreatedByAdminId: createdByAdminId,
            TitleAr: body.title_ar,
            TitleEn: body.title_en,
            SubjectId: body.subject_id,
            Grade: body.grade,
            TopicBindings: body.topic_bindings ?? new List<string>(),
            DurationMinutes: body.duration_minutes,
            ScheduledStart: body.scheduled_start,
            ScheduledEnd: body.scheduled_end,
            Questions: body.questions.Select(MapQuestion).ToList(),
            ClassGroupIds: body.class_group_ids,
            CorrelationId: correlationId);

        try
        {
            var result = await service.CreateAsync(input, ct);
            http.Response.Headers["X-Correlation-Id"] = correlationId;
            return Results.Created(
                uri: $"/api/school-admin/exams/{result.Exam.ExamId}",
                value: new
                {
                    exam_id = result.Exam.ExamId,
                    status = result.Exam.Status,
                    question_count = result.Questions.Count,
                    total_points = result.Exam.TotalPoints,
                    class_group_ids = result.Assignments.Select(a => a.ClassGroupId).ToList(),
                });
        }
        catch (CustomQuestionRejectedException ex)
        {
            return Results.BadRequest(new { error = ex.Message, violations = ex.Violations });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    public static async Task<IResult> HandleListBySchoolAsync(
        HttpContext http,
        IExamRepository repo,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetSchoolAdminId(http, out _))
        {
            return Results.Unauthorized();
        }
        var rows = await repo.ListForSchoolAsync(tenantId, schoolTenantId, ct);
        return Results.Ok(new { exams = rows.Select(Project).ToList() });
    }

    public static async Task<IResult> HandleListByTeacherAsync(
        HttpContext http,
        IExamRepository repo,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetTeacherId(http, out var teacherId))
        {
            return Results.Unauthorized();
        }
        var rows = await repo.ListForTeacherAsync(tenantId, schoolTenantId, teacherId, ct);
        return Results.Ok(new { exams = rows.Select(Project).ToList() });
    }

    public static async Task<IResult> HandleScheduleAsync(
        Guid examId,
        HttpContext http,
        ScheduleRequest body,
        IExamRepository repo,
        CancellationToken ct)
    {
        if (!TryResolveAdminScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();
        var exam = await repo.GetByIdAsync(tenantId, schoolTenantId, examId, ct);
        if (exam is null) return Results.NotFound(new { error = "exam_not_found" });
        try
        {
            exam.Status = ExamStateMachine.Transition(exam.Status, ExamStates.Scheduled);
        }
        catch (InvalidExamStateTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        exam.ScheduledStart = body.scheduled_start;
        exam.ScheduledEnd = body.scheduled_end;
        exam.DurationMinutes = body.duration_minutes ?? exam.DurationMinutes;
        exam.UpdatedAt = DateTime.UtcNow;
        await repo.SaveChangesAsync(ct);
        return Results.Ok(Project(exam));
    }

    public static async Task<IResult> HandleOpenAsync(
        Guid examId,
        HttpContext http,
        IExamRepository repo,
        IExamQuestionRepository questions,
        CancellationToken ct)
    {
        if (!TryResolveAdminScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();
        var exam = await repo.GetByIdAsync(tenantId, schoolTenantId, examId, ct);
        if (exam is null) return Results.NotFound(new { error = "exam_not_found" });
        var qList = await questions.ListForExamAsync(tenantId, examId, ct);
        if (qList.Count == 0)
        {
            return Results.Conflict(new { error = "exam_has_no_questions" });
        }
        try
        {
            exam.Status = ExamStateMachine.Transition(exam.Status, ExamStates.Open);
        }
        catch (InvalidExamStateTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        exam.UpdatedAt = DateTime.UtcNow;
        await repo.SaveChangesAsync(ct);
        return Results.Ok(Project(exam));
    }

    public static async Task<IResult> HandleCloseAsync(
        Guid examId,
        HttpContext http,
        IExamRepository repo,
        CancellationToken ct)
    {
        if (!TryResolveAdminScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();
        var exam = await repo.GetByIdAsync(tenantId, schoolTenantId, examId, ct);
        if (exam is null) return Results.NotFound(new { error = "exam_not_found" });
        try
        {
            exam.Status = ExamStateMachine.Transition(exam.Status, ExamStates.Closed);
        }
        catch (InvalidExamStateTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        exam.UpdatedAt = DateTime.UtcNow;
        await repo.SaveChangesAsync(ct);
        return Results.Ok(Project(exam));
    }

    private static ExamQuestionInput MapQuestion(ExamQuestionPayload p)
        => new(
            QuestionSource: string.IsNullOrWhiteSpace(p.question_source) ? "phase1_content" : p.question_source,
            Phase1ContentId: p.phase1_content_id,
            QuestionTextAr: p.question_text_ar,
            QuestionTextEn: p.question_text_en,
            QuestionType: p.question_type,
            OptionsJson: p.options is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null }
                ? p.options.Value.GetRawText()
                : null,
            CorrectAnswerJson: p.correct_answer.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : p.correct_answer.GetRawText(),
            Points: p.points);

    private static bool TryResolveAdminScope(HttpContext http, out Guid tenantId, out Guid schoolTenantId)
    {
        schoolTenantId = Guid.Empty;
        return SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
            && SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId)
            && SchoolManagementHeaders.TryGetSchoolAdminId(http, out _);
    }

    private static object Project(Exam e) => new
    {
        exam_id = e.ExamId,
        title_ar = e.TitleAr,
        title_en = e.TitleEn,
        subject_id = e.SubjectId,
        grade = e.Grade,
        status = e.Status,
        duration_minutes = e.DurationMinutes,
        scheduled_start = e.ScheduledStart,
        scheduled_end = e.ScheduledEnd,
        total_points = e.TotalPoints,
        created_at = e.CreatedAt,
    };
}
