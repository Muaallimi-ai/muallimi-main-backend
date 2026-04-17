using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.StudentSession;

namespace Muallimi.Api.StudentExperience.PlanGating;

/// <summary>
/// T034 (US1) — GET /student/plan-gate/snapshot.
///
/// Returns the current plan-gate tile states for the active session so the
/// frontend can render tile locks on the first streamed frame. The snapshot
/// carries an <c>expires_at</c> that the UI uses as a hint; every gated
/// entry point MUST re-check at request time regardless.
/// </summary>
public static class PlanGateSnapshotEndpoint
{
    public const string Route = "/api/student/plan-gate/snapshot";

    // 30 seconds mirrors PlanGateResolver's in-process cache TTL so the
    // UI and server agree on refresh cadence.
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapPlanGateSnapshot(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, async (
                HttpContext http,
                Guid session_id,
                IStudentSessionRepository sessions,
                IHomeDashboardService dashboard,
                CancellationToken ct) =>
            {
                if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
                    return Results.Unauthorized();

                var session = await sessions.FindAsync(session_id, ct);
                if (session is null || session.TenantId != tenantId)
                    return Results.NotFound();

                var tiles = await dashboard.ResolveTilesAsync(tenantId, session.PlanTierSnapshot, ct);
                return Results.Ok(new PlanGateSnapshotResponse(
                    SessionId: session.Id,
                    PlanTierSnapshot: session.PlanTierSnapshot,
                    ModeTileStates: tiles,
                    ExpiresAt: DateTime.UtcNow.Add(SnapshotTtl)));
            })
            .WithName("StudentPlanGateSnapshot")
            .WithTags("StudentExperience");
        return routes;
    }
}
