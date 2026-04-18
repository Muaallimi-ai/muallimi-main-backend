using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.OperatorImpersonation;

namespace Muallimi.Api.Parents.ParentDashboard;

/// <summary>
/// T072 (US2) — GET /parent/children.
///
/// Returns the list of children linked to the authenticated parent in the
/// active tenant. Feeds the child-selector component on the parent
/// dashboard. Always filters by the active <see cref="Muallimi.Domain.Parents.ChildLink"/>
/// so a revoked link (effective_end in the past) disappears from the
/// selector in the next render.
///
/// When operator impersonation headers are present, writes a
/// <see cref="Muallimi.Domain.Parents.OperatorImpersonationAudit"/> row for
/// the selector surface (logical <c>parent_dashboard</c> surface) so every
/// impersonated render of the selector is audited.
/// </summary>
public static class ParentChildrenEndpoint
{
    public const string Route = "/api/parent/children";

    public static IEndpointRouteBuilder MapParentChildren(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("ParentChildren")
            .WithTags("ParentDashboard");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        IParentDashboardService service,
        IOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var children = await service.ListChildrenAsync(tenantId, parentProfileId, ct);

        if (ParentDashboardHeaders.TryGetOperatorContext(http, out var operatorActorId, out var reason))
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                targetParentProfileId: parentProfileId,
                targetChildId: null,
                surface: OperatorImpersonationSurfaces.ParentDashboard,
                reason: string.IsNullOrWhiteSpace(reason) ? "child_selector" : reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;

        return Results.Ok(new
        {
            children = children.Select(c => new
            {
                child_id = c.ChildId,
                display_name = c.DisplayName,
                curriculum_type = c.CurriculumType,
                grade = c.Grade,
                preferred_language = c.PreferredLanguage,
            }),
            correlation_id = correlationId,
        });
    }
}
