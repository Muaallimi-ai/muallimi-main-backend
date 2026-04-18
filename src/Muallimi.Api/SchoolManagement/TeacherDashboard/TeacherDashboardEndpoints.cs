using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

namespace Muallimi.Api.SchoolManagement.TeacherDashboard;

/// <summary>
/// T107 (US5) — GET <c>/api/teacher/dashboard</c>.
///
/// Returns the list of assigned (class, subject) pairs for the
/// authenticated teacher. Role scoping is enforced by
/// <see cref="ITeacherDashboardService"/> — unassigned classes or subjects
/// do not appear. Billing and family-private data are NOT projected
/// (FR-018, CR-001). Operator-impersonated requests write an audit row
/// against the <c>teacher_dashboard</c> surface.
/// </summary>
public static class TeacherDashboardEndpoints
{
    public const string DashboardRoute = "/api/teacher/dashboard";

    public static IEndpointRouteBuilder MapTeacherDashboard(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(DashboardRoute, HandleAsync)
            .WithName("GetTeacherDashboard")
            .WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
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

        var response = await service.GetTeacherDashboardAsync(tenantId, schoolTenantId, teacherId, ct);
        if (response is null) return Results.NotFound(new { error = "teacher_not_found" });

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);

        if (SchoolManagementHeaders.TryGetOperatorActorId(http, out var operatorActorId))
        {
            var reason = http.Request.Headers["X-Operator-Reason"].ToString();
            if (string.IsNullOrWhiteSpace(reason)) reason = "teacher_dashboard_view";
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
            teacher_id = response.TeacherId,
            assigned_classes = response.AssignedClasses.Select(c => new
            {
                class_group_id = c.ClassGroupId,
                display_name_ar = c.ClassDisplayNameAr,
                display_name_en = c.ClassDisplayNameEn,
                subject_id = c.SubjectId,
                subject_name_ar = c.SubjectNameAr,
                subject_name_en = c.SubjectNameEn,
                student_count = c.StudentCount,
                average_mastery = c.AverageMastery,
                at_risk_count = c.AtRiskCount,
            }),
        });
    }
}
