using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.StudentProgressSurface;

/// <summary>
/// Phase 4 — Student Progress Surface module marker. Owns the student-
/// authenticated progress facade (mastery, streak, badges, focus areas).
/// </summary>
public static class StudentProgressSurfaceModule
{
    public static IEndpointRouteBuilder MapStudentProgressSurface(this IEndpointRouteBuilder routes)
    {
        routes.MapStudentProgressSummary();
        routes.MapFocusAreaDetail();
        routes.MapBadgeCelebrationShown();
        return routes;
    }
}
