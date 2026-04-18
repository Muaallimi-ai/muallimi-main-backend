using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.ParentDashboard;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T096 (US3) — GET /reports/share/{share_token}.
///
/// Resolves a share token, validates it against the per-tenant key, and
/// renders ONLY the specific report. No broader dashboard access is
/// granted: the handler never accepts a parent profile header and never
/// consults <see cref="IChildLinkRepository"/>. Token signing keys are
/// per-tenant; a token issued in one tenant cannot open a report in
/// another tenant even if the attacker can guess the report id.
/// </summary>
public static class SharedReportViewEndpoint
{
    public const string Route = "/api/reports/share/{shareToken}";

    public static IEndpointRouteBuilder MapSharedReportView(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("SharedReportView")
            .WithTags("WeeklyReport");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        string shareToken,
        HttpContext http,
        [Microsoft.AspNetCore.Mvc.FromServices] IShareTokenValidator tokens,
        IWeeklyReportRepository reports,
        CancellationToken ct)
    {
        if (!tokens.TryParse(shareToken, out var claims)) return Results.NotFound();

        var report = await reports.GetByIdAsync(claims.TenantId, claims.WeeklyReportId, ct);
        if (report is null) return Results.NotFound();

        var expectedHash = tokens.HashForStorage(shareToken);
        if (!string.Equals(report.ShareTokenHash, expectedHash, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        if (report.Status != "ready")
        {
            return Results.NotFound();
        }

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        http.Response.Headers["X-Correlation-Id"] = correlationId;

        var payload = WeeklyReportProjection.Project(report, correlationId);
        return Results.Ok(WeeklyReportProjection.ToWire(payload));
    }
}
