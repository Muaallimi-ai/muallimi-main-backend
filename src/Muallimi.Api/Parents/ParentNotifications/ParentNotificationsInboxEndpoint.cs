using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentDashboard;

namespace Muallimi.Api.Parents.ParentNotifications;

/// <summary>
/// T131 (US7) — <c>GET /parent/notifications</c>.
///
/// Returns the inbox for the authenticated parent, filtered to the active
/// <see cref="Muallimi.Domain.Parents.ChildLink"/> set. Rows from children
/// outside the parent's active link set are never surfaced, even in the
/// same tenant — the parent-dashboard contract ("A parent sees only their
/// own linked children's data") applies to notifications as well.
/// </summary>
public static class ParentNotificationsInboxEndpoint
{
    public const string Route = "/api/parent/notifications";
    public const string MarkReadRoute = "/api/parent/notifications/{notificationId:guid}/mark-read";
    private const int DefaultInboxLimit = 50;

    public static IEndpointRouteBuilder MapParentNotificationsInbox(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleListAsync)
            .WithName("ParentNotificationsInbox")
            .WithTags("ParentNotifications");
        routes.MapPost(MarkReadRoute, HandleMarkReadAsync)
            .WithName("ParentNotificationMarkRead")
            .WithTags("ParentNotifications");
        return routes;
    }

    public static async Task<IResult> HandleListAsync(
        HttpContext http,
        IParentNotificationRepository notifications,
        IChildLinkRepository links,
        IOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var isImpersonation = ParentDashboardHeaders.TryGetOperatorContext(http, out var operatorActorId, out var reason);

        var activeLinks = await links.ListActiveForParentAsync(tenantId, parentProfileId, ct);
        var allowedChildIds = activeLinks.Select(l => l.StudentId).ToArray();

        var rows = await notifications.ListForParentAsync(
            tenantId, parentProfileId, allowedChildIds, DefaultInboxLimit, ct);

        if (isImpersonation)
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                targetParentProfileId: parentProfileId,
                targetChildId: null,
                surface: OperatorImpersonationSurfaces.ParentNotifications,
                reason: string.IsNullOrWhiteSpace(reason) ? "notifications_view" : reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;

        return Results.Ok(new
        {
            notifications = rows.Select(n => new
            {
                parent_notification_id = n.ParentNotificationId,
                child_id = n.ChildId,
                notification_kind = n.NotificationKind,
                channel = n.Channel,
                language = n.Language,
                body_ar = n.BodyAr,
                body_en = n.BodyEn,
                delivery_state = n.DeliveryState,
                quiet_hours_deferred_until = n.QuietHoursDeferredUntil,
                dispatched_at = n.DispatchedAt,
                correlation_id = n.CorrelationId,
                created_at = n.CreatedAt,
            }),
            correlation_id = correlationId,
        });
    }

    public static async Task<IResult> HandleMarkReadAsync(
        Guid notificationId,
        HttpContext http,
        IParentNotificationRepository notifications,
        IChildLinkRepository links,
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var notification = await notifications.GetAsync(tenantId, parentProfileId, notificationId, ct);
        if (notification is null) return Results.NotFound();

        var link = await links.GetActiveAsync(tenantId, parentProfileId, notification.ChildId, ct);
        if (link is null) return Results.NotFound();

        if (notification.DeliveryState == "dispatched" || notification.DeliveryState == "deferred")
        {
            notification.DeliveryState = "read";
            await notifications.UpdateAsync(notification, ct);
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }
}
