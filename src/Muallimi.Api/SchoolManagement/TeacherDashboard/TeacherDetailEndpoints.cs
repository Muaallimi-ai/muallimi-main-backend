using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

namespace Muallimi.Api.SchoolManagement.TeacherDashboard;

/// <summary>
/// T108 (US5) — Teacher-scoped detail endpoints.
///
/// Both routes gate on an active
/// <see cref="Muallimi.Domain.SchoolManagement.TeacherAssignment"/>:
///
///   • GET <c>/api/teacher/dashboard/class/{classGroupId}/subject/{subjectId}</c>
///     returns per-student mastery + focus-area + at-risk rows for the
///     (class, subject) the teacher is explicitly assigned to.
///   • GET <c>/api/teacher/dashboard/student/{studentId}</c> returns the
///     student view constrained to subjects the teacher is assigned to
///     teach THIS student's class. Billing, plan tier, and
///     family-private fields are never surfaced (FR-018, CR-001).
///
/// Operator impersonation writes an audit row against the
/// <c>teacher_dashboard</c> surface.
/// </summary>
public static class TeacherDetailEndpoints
{
    public const string ClassSubjectRoute = "/api/teacher/dashboard/class/{classGroupId:guid}/subject/{subjectId:guid}";
    public const string StudentRoute = "/api/teacher/dashboard/student/{studentId:guid}";

    public static IEndpointRouteBuilder MapTeacherDashboardDetail(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ClassSubjectRoute, HandleClassSubjectAsync)
            .WithName("GetTeacherClassSubjectDetail")
            .WithTags("SchoolManagement");
        routes.MapGet(StudentRoute, HandleStudentAsync)
            .WithName("GetTeacherStudentDetail")
            .WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleClassSubjectAsync(
        Guid classGroupId,
        Guid subjectId,
        HttpContext http,
        ITeacherDashboardService service,
        ISchoolOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetTeacherId(http, out var teacherId))
        {
            return Results.Unauthorized();
        }

        var response = await service.GetClassSubjectDetailAsync(
            tenantId, schoolTenantId, teacherId, classGroupId, subjectId, ct);
        if (response is null) return Results.Forbid();

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);

        if (SchoolManagementHeaders.TryGetOperatorActorId(http, out var operatorActorId))
        {
            var reason = http.Request.Headers["X-Operator-Reason"].ToString();
            if (string.IsNullOrWhiteSpace(reason)) reason = "teacher_class_subject_view";
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                schoolTenantId: schoolTenantId,
                targetUserIdentityId: teacherId,
                surface: SchoolOperatorImpersonationSurfaces.TeacherDashboard,
                reason: reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(new
        {
            class_group_id = response.ClassGroupId,
            subject_id = response.SubjectId,
            students = response.Students.Select(s => new
            {
                student_id = s.StudentId,
                display_name_ar = s.DisplayNameAr,
                display_name_en = s.DisplayNameEn,
                mastery_score = s.MasteryScore,
                mastery_band = s.MasteryBand,
                focus_areas = s.FocusAreas.Select(f => new
                {
                    topic_name_ar = f.TopicNameAr,
                    topic_name_en = f.TopicNameEn,
                    rationale_ar = f.RationaleAr,
                    rationale_en = f.RationaleEn,
                }),
                at_risk = s.AtRisk,
                streak_length = s.StreakLength,
                last_activity_at = s.LastActivityAt,
            }),
        });
    }

    public static async Task<IResult> HandleStudentAsync(
        Guid studentId,
        HttpContext http,
        ITeacherDashboardService service,
        ISchoolOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetTeacherId(http, out var teacherId))
        {
            return Results.Unauthorized();
        }

        var response = await service.GetStudentDetailAsync(
            tenantId, schoolTenantId, teacherId, studentId, ct);
        if (response is null) return Results.Forbid();

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);

        if (SchoolManagementHeaders.TryGetOperatorActorId(http, out var operatorActorId))
        {
            var reason = http.Request.Headers["X-Operator-Reason"].ToString();
            if (string.IsNullOrWhiteSpace(reason)) reason = "teacher_student_detail_view";
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                schoolTenantId: schoolTenantId,
                targetUserIdentityId: teacherId,
                surface: SchoolOperatorImpersonationSurfaces.TeacherDashboard,
                reason: reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(new
        {
            student_id = response.StudentId,
            display_name_ar = response.DisplayNameAr,
            display_name_en = response.DisplayNameEn,
            mastery = response.Mastery.Select(m => new
            {
                subject_id = m.SubjectId,
                subject_name_ar = m.SubjectNameAr,
                subject_name_en = m.SubjectNameEn,
                mastery_score = m.MasteryScore,
                mastery_band = m.MasteryBand,
                topics = m.Topics.Select(t => new
                {
                    topic_id = t.TopicId,
                    topic_name_ar = t.TopicNameAr,
                    topic_name_en = t.TopicNameEn,
                    mastery_score = t.MasteryScore,
                }),
            }),
            focus_areas = response.FocusAreas.Select(f => new
            {
                topic_name_ar = f.TopicNameAr,
                topic_name_en = f.TopicNameEn,
                rationale_ar = f.RationaleAr,
                rationale_en = f.RationaleEn,
                deep_link = f.DeepLink,
            }),
            streak_length = response.StreakLength,
            badges = response.Badges.Select(b => new
            {
                badge_key = b.BadgeKey,
                badge_name_ar = b.BadgeNameAr,
                badge_name_en = b.BadgeNameEn,
                awarded_at = b.AwardedAt,
            }),
            at_risk = response.AtRisk,
            intervention_prompt = response.InterventionPrompt is null
                ? null
                : new
                {
                    body_ar = response.InterventionPrompt.BodyAr,
                    body_en = response.InterventionPrompt.BodyEn,
                    next_step = new
                    {
                        phase3_mode = response.InterventionPrompt.NextStepPhase3Mode,
                        deep_link = response.InterventionPrompt.NextStepDeepLink,
                    },
                },
        });
    }
}
