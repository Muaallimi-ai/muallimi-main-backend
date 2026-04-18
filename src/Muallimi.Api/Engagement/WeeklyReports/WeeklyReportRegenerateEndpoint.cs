using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.ParentDashboard;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T097 (US3) — POST /parent/reports/{report_id}/regenerate.
///
/// Triggers an explicit regeneration inside the reporting window. The
/// previous row is flipped to <c>regenerating</c>, a new <c>run_id</c>
/// is assigned, and the generator re-runs through the Phase 2 guardrail
/// chain. Duplicate suppression: a caller that triggers regenerate while
/// another regeneration is in flight receives <c>409 Conflict</c> with
/// the in-flight report id so clients can poll instead of stacking
/// requests.
/// </summary>
public static class WeeklyReportRegenerateEndpoint
{
    public const string Route = "/api/parent/reports/{reportId:guid}/regenerate";

    public static IEndpointRouteBuilder MapWeeklyReportRegenerate(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleAsync)
            .WithName("WeeklyReportRegenerate")
            .WithTags("WeeklyReport");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        Guid reportId,
        HttpContext http,
        IWeeklyReportRepository reports,
        IChildLinkRepository links,
        IWeeklyReportGenerator generator,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var existing = await reports.GetByIdAsync(tenantId, reportId, ct);
        if (existing is null) return Results.NotFound();

        var link = await links.GetActiveAsync(tenantId, parentProfileId, existing.StudentId, ct);
        if (link is null) return Results.NotFound();

        if (existing.Status == "generating" || existing.Status == "regenerating")
        {
            return Results.Conflict(new
            {
                error = "regeneration_in_flight",
                weekly_report_id = existing.WeeklyReportId,
                status = existing.Status,
            });
        }

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var outcome = await generator.GenerateAsync(
            tenantId: tenantId,
            studentId: existing.StudentId,
            windowStart: existing.WindowStart,
            windowEnd: existing.WindowEnd,
            correlationId: correlationId,
            forceRegenerate: true,
            ct: ct);

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(new
        {
            weekly_report_id = outcome.WeeklyReportId,
            outcome = outcome.Outcome.ToString().ToLowerInvariant(),
        });
    }
}
