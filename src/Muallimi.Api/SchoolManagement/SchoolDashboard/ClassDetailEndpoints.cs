using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

namespace Muallimi.Api.SchoolManagement.SchoolDashboard;

/// <summary>
/// T092 + T093 (US4) — GET <c>/api/school-admin/dashboard/class/{classGroupId}</c>.
///
/// Returns per-student mastery, streak, badge, focus-area, and at-risk
/// indicators for the requested class, scoped to the authenticated school
/// admin. Operator-impersonated requests write a class-scoped audit row
/// (surface = <c>school_admin_classes</c>) via
/// <see cref="ISchoolOperatorImpersonationAuditor"/>.
/// </summary>
public static class ClassDetailEndpoints
{
    public const string ClassDetailRoute = "/api/school-admin/dashboard/class/{classGroupId:guid}";

    public static IEndpointRouteBuilder MapSchoolAdminClassDetail(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ClassDetailRoute, HandleAsync)
            .WithName("GetSchoolAdminClassDetail")
            .WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        Guid classGroupId,
        HttpContext http,
        ISchoolDashboardService service,
        ISchoolOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetSchoolAdminId(http, out var schoolAdminId))
        {
            return Results.Unauthorized();
        }

        var response = await service.GetClassDetailAsync(tenantId, schoolTenantId, classGroupId, ct);
        if (response is null) return Results.NotFound(new { error = "class_not_found" });

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);

        if (SchoolManagementHeaders.TryGetOperatorActorId(http, out var operatorActorId))
        {
            var reason = http.Request.Headers["X-Operator-Reason"].ToString();
            if (string.IsNullOrWhiteSpace(reason)) reason = "school_admin_class_detail_view";
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                schoolTenantId: schoolTenantId,
                targetUserIdentityId: schoolAdminId,
                surface: SchoolOperatorImpersonationSurfaces.SchoolAdminClasses,
                reason: reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(new
        {
            class_group_id = response.ClassGroupId,
            display_name_ar = response.DisplayNameAr,
            display_name_en = response.DisplayNameEn,
            student_count = response.StudentCount,
            students = response.Students.Select(s => new
            {
                student_id = s.StudentId,
                display_name_ar = s.DisplayNameAr,
                display_name_en = s.DisplayNameEn,
                mastery_summary = s.MasterySummary.Select(m => new
                {
                    subject_id = m.SubjectId,
                    subject_name_ar = SubjectCatalogue.ArabicName(m.SubjectId),
                    subject_name_en = SubjectCatalogue.EnglishName(m.SubjectId),
                    mastery_score = m.MasteryScore,
                    mastery_band = m.MasteryBand,
                }),
                streak_length = s.StreakLength,
                badges_earned = s.BadgesEarned,
                at_risk = s.AtRisk,
                focus_areas_count = s.FocusAreasCount,
                last_activity_at = s.LastActivityAt,
            }),
            class_mastery = response.ClassMastery.Select(cm => new
            {
                subject_id = cm.SubjectId,
                average_mastery = cm.AverageMastery,
            }),
        });
    }
}
