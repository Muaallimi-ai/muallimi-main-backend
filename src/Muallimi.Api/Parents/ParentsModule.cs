using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Api.Parents.ParentPreferences;

namespace Muallimi.Api.Parents;

/// <summary>
/// Phase 4 — Parents module marker. Owns the parent dashboard, preferences,
/// notifications, and operator impersonation surfaces. Registrations are wired
/// per user story as endpoints ship.
/// </summary>
public static class ParentsModule
{
    public static IEndpointRouteBuilder MapParents(this IEndpointRouteBuilder routes)
    {
        // US2 — Parent dashboard + child selector.
        routes.MapParentChildren();
        routes.MapParentDashboard();
        // US7 — Parent notifications inbox + mark-read + preferences.
        routes.MapParentNotificationsInbox();
        routes.MapParentPreferences();
        return routes;
    }
}
