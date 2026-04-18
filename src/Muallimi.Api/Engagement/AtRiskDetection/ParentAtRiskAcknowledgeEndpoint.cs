using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T151 (US8) — POST /api/parent/at-risk/{flag_id}/acknowledge.
///
/// Lets the parent acknowledge an active at-risk flag without dismissing
/// the linked intervention prompt. The acknowledgement is recorded on the
/// flag (<c>AcknowledgedAt</c>, <c>AcknowledgedByParentProfileId</c>) so
/// downstream surfaces can render a calmer affordance, but the flag itself
/// stays active. Manual clearing is intentionally not exposed — recovery
/// is computed by <see cref="AtRiskDetectionOrchestrator"/>.
///
/// Tenant isolation: cross-tenant or non-linked-child requests return
/// 404 (not 403) to avoid leaking flag existence.
/// </summary>
public static class ParentAtRiskAcknowledgeEndpoint
{
    public const string Route = "/api/parent/at-risk/{flagId:guid}/acknowledge";

    public static IEndpointRouteBuilder MapParentAtRiskAcknowledge(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleAsync)
            .WithName("ParentAtRiskAcknowledge")
            .WithTags("AtRisk");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        Guid flagId,
        HttpContext http,
        IAtRiskFlagRepository flags,
        IChildLinkRepository links,
        IOperatorImpersonationAuditor auditor,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var isImpersonation = ParentDashboardHeaders.TryGetOperatorContext(
            http, out var operatorActorId, out var reason);

        var flag = await flags.GetByIdAsync(tenantId, flagId, ct);
        if (flag is null) return Results.NotFound();

        var link = await links.GetActiveAsync(tenantId, parentProfileId, flag.StudentId, ct);
        if (link is null) return Results.NotFound();

        if (flag.ClearedAt.HasValue)
        {
            return Results.Conflict(new { reason = "flag_cleared" });
        }

        await flags.AcknowledgeAsync(flag, parentProfileId, DateTime.UtcNow, ct);

        if (isImpersonation)
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                targetParentProfileId: parentProfileId,
                targetChildId: flag.StudentId,
                surface: OperatorImpersonationSurfaces.InterventionPrompt,
                reason: string.IsNullOrWhiteSpace(reason) ? "at_risk_acknowledge" : reason,
                correlationId: correlationId,
                ct: ct);
        }

        await db.SaveChangesAsync(ct);

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(new
        {
            at_risk_flag_id = flag.AtRiskFlagId,
            acknowledged_at = flag.AcknowledgedAt,
            acknowledged_by_parent_profile_id = flag.AcknowledgedByParentProfileId,
            correlation_id = correlationId,
        });
    }
}
