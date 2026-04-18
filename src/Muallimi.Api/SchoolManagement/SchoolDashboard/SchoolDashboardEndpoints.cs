using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

namespace Muallimi.Api.SchoolManagement.SchoolDashboard;

/// <summary>
/// T091 + T093 (US4) — GET <c>/api/school-admin/dashboard</c>.
///
/// Returns the school-wide aggregate dashboard payload for the authenticated
/// school admin. The endpoint is tenant + school-tenant + admin scoped via
/// the standard Phase 5 headers; when the request also carries
/// <c>X-Operator-Actor-Id</c> the
/// <see cref="ISchoolOperatorImpersonationAuditor"/> writes a single audit row
/// for the render. The audit write is unconditional on the impersonation
/// path — a successful 200 MUST have a paired audit row (readiness gate
/// invariant, FR-021/CR-001).
/// </summary>
public static class SchoolDashboardEndpoints
{
    public const string DashboardRoute = "/api/school-admin/dashboard";

    public static IEndpointRouteBuilder MapSchoolAdminDashboard(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(DashboardRoute, HandleAsync)
            .WithName("GetSchoolAdminDashboard")
            .WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
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

        var response = await service.GetSchoolDashboardAsync(tenantId, schoolTenantId, ct);
        if (response is null) return Results.NotFound(new { error = "school_not_found" });

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);

        if (SchoolManagementHeaders.TryGetOperatorActorId(http, out var operatorActorId))
        {
            var reason = http.Request.Headers["X-Operator-Reason"].ToString();
            if (string.IsNullOrWhiteSpace(reason)) reason = "school_admin_dashboard_view";
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                schoolTenantId: schoolTenantId,
                targetUserIdentityId: schoolAdminId,
                surface: SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
                reason: reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(new
        {
            school_tenant_id = response.SchoolTenantId,
            school_name_ar = response.SchoolNameAr,
            school_name_en = response.SchoolNameEn,
            total_students = response.TotalStudents,
            active_students = response.ActiveStudents,
            school_mastery = response.SchoolMastery.Select(m => new
            {
                grade = m.Grade,
                subject_id = m.SubjectId,
                subject_name_ar = SubjectCatalogue.ArabicName(m.SubjectId),
                subject_name_en = SubjectCatalogue.EnglishName(m.SubjectId),
                average_mastery = m.AverageMastery,
                mastery_band = m.MasteryBand,
            }),
            engagement = new
            {
                average_session_frequency = response.Engagement.AverageSessionFrequency,
                active_streak_count = response.Engagement.ActiveStreakCount,
                badges_awarded_count = response.Engagement.BadgesAwardedCount,
            },
            at_risk = new
            {
                total_at_risk = response.AtRisk.TotalAtRisk,
                per_class = response.AtRisk.PerClass.Select(pc => new
                {
                    class_group_id = pc.ClassGroupId,
                    display_name_ar = pc.DisplayNameAr,
                    display_name_en = pc.DisplayNameEn,
                    at_risk_count = pc.AtRiskCount,
                }),
            },
            recent_reports_count = response.RecentReportsCount,
            last_updated_at = response.LastUpdatedAt,
        });
    }
}

/// <summary>
/// Minimal subject-id → bilingual name catalogue. Real localisation lives in
/// the Phase 1 curriculum service; for US4 we cover the dashboard subjects
/// used in local-parity walkthroughs and fall back to echoing the raw id.
/// </summary>
internal static class SubjectCatalogue
{
    public static string ArabicName(string subjectId) => subjectId?.ToLowerInvariant() switch
    {
        "math" => "الرياضيات",
        "arabic" => "اللغة العربية",
        "english" => "اللغة الإنجليزية",
        "science" => "العلوم",
        _ => subjectId ?? string.Empty,
    };

    public static string EnglishName(string subjectId) => subjectId?.ToLowerInvariant() switch
    {
        "math" => "Mathematics",
        "arabic" => "Arabic",
        "english" => "English",
        "science" => "Science",
        _ => subjectId ?? string.Empty,
    };
}
