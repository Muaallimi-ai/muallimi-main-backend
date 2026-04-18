using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Engagement.AtRiskDetection;
using Muallimi.Api.Engagement.WeeklyReports;

namespace Muallimi.Api.Engagement;

/// <summary>
/// Phase 4 — Engagement module marker. Subfolders own their endpoints,
/// services, repositories, and background services. Registrations are wired
/// per user story as endpoints ship.
/// </summary>
public static class EngagementModule
{
    public static IEndpointRouteBuilder MapEngagement(this IEndpointRouteBuilder routes)
    {
        // US3 — Weekly report generation, view, share, regenerate.
        routes.MapWeeklyReportView();
        routes.MapWeeklyReportShareLink();
        routes.MapWeeklyReportRegenerate();
        routes.MapSharedReportView();
        // US8 — At-risk detection + intervention prompts.
        routes.MapParentAtRisk();
        routes.MapStudentAtRiskSelf();
        routes.MapParentAtRiskAcknowledge();
        return routes;
    }
}
