using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T095 (US3) — POST /parent/reports/{report_id}/share-link.
///
/// Issues a per-tenant HMAC-signed token with a short TTL bounded by
/// <see cref="ShareTokenValidator.MaxTtl"/>. The token embeds the
/// tenant_id AND the report_id so a token issued in one tenant fails
/// validation in another tenant (per-tenant signing key). The token
/// hash is persisted on the <see cref="Muallimi.Domain.Engagement.WeeklyReport.ShareTokenHash"/>
/// column so later revocation can look it up without storing the plaintext
/// token itself.
/// </summary>
public static class WeeklyReportShareLinkEndpoint
{
    public const string Route = "/api/parent/reports/{reportId:guid}/share-link";
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(72);

    public static IEndpointRouteBuilder MapWeeklyReportShareLink(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleAsync)
            .WithName("WeeklyReportShareLink")
            .WithTags("WeeklyReport");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        Guid reportId,
        HttpContext http,
        IWeeklyReportRepository reports,
        IChildLinkRepository links,
        [Microsoft.AspNetCore.Mvc.FromServices] IShareTokenValidator tokens,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var report = await reports.GetByIdAsync(tenantId, reportId, ct);
        if (report is null) return Results.NotFound();

        var link = await links.GetActiveAsync(tenantId, parentProfileId, report.StudentId, ct);
        if (link is null) return Results.NotFound();

        if (report.Status != "ready") return Results.Conflict(new { error = "report_not_ready" });

        var issued = tokens.Issue(tenantId, reportId, DefaultTtl);
        report.ShareTokenHash = tokens.HashForStorage(issued.RawToken);
        await reports.UpdateAsync(report, ct);
        await db.SaveChangesAsync(ct);

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        http.Response.Headers["X-Correlation-Id"] = correlationId;

        return Results.Ok(new
        {
            share_token = issued.RawToken,
            expires_at = issued.ExpiresAt,
        });
    }
}
