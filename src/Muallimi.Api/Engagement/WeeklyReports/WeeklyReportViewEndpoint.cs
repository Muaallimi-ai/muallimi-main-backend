using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentDashboard;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T094 (US3) — GET /parent/reports/{report_id}.
///
/// Returns the weekly report in both languages for viewer rendering.
/// Tenant isolation: the calling parent's tenant is matched against the
/// stored <c>tenant_id</c>; a cross-tenant lookup returns 404 (not 403)
/// so cross-family existence cannot be probed. Operator impersonation is
/// audited on every render (same transactional contract as T073).
/// </summary>
public static class WeeklyReportViewEndpoint
{
    public const string Route = "/api/parent/reports/{reportId:guid}";

    public static IEndpointRouteBuilder MapWeeklyReportView(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("WeeklyReportView")
            .WithTags("WeeklyReport");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        Guid reportId,
        HttpContext http,
        IWeeklyReportRepository reports,
        IChildLinkRepository links,
        IOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var isImpersonation = ParentDashboardHeaders.TryGetOperatorContext(http, out var operatorActorId, out var reason);

        var report = await reports.GetByIdAsync(tenantId, reportId, ct);
        if (report is null) return Results.NotFound();

        var link = await links.GetActiveAsync(tenantId, parentProfileId, report.StudentId, ct);
        if (link is null) return Results.NotFound();

        if (isImpersonation)
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                targetParentProfileId: parentProfileId,
                targetChildId: report.StudentId,
                surface: OperatorImpersonationSurfaces.WeeklyReportViewer,
                reason: string.IsNullOrWhiteSpace(reason) ? "weekly_report_view" : reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        var payload = WeeklyReportProjection.Project(report, correlationId);
        return Results.Ok(WeeklyReportProjection.ToWire(payload));
    }
}
