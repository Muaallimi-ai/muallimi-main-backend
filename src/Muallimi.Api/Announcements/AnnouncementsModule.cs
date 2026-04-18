using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Announcements.AnnouncementCreation;
using Muallimi.Api.Announcements.AnnouncementQuery;

namespace Muallimi.Api.Announcements;

/// <summary>
/// Phase 5 — Announcements module marker. US8 (T153–T168) wires school
/// admin CRUD + publish + delivery-report endpoints alongside the student
/// and parent inbox endpoints. The dispatcher + scheduler are background
/// services registered through the Phase 5 DI extensions in
/// <c>Program.cs</c>.
/// </summary>
public static class AnnouncementsModule
{
    public static IEndpointRouteBuilder MapAnnouncements(this IEndpointRouteBuilder routes)
    {
        routes.MapAnnouncementAdmin();
        routes.MapAnnouncementInbox();
        return routes;
    }
}
