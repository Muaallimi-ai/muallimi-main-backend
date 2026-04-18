using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Announcements.AnnouncementCreation;
using Muallimi.Api.Leaderboards.LeaderboardQuery;
using Muallimi.Api.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Announcements.AnnouncementQuery;

/// <summary>
/// T162 (US8) — student + parent announcement inbox endpoints.
///
/// Routes:
///   • GET  /api/student/announcements                       → student inbox
///   • POST /api/student/announcements/{deliveryId}/read     → mark as read
///   • GET  /api/parent/announcements/{childId}              → parent inbox for a specific child
///   • POST /api/parent/announcements/{deliveryId}/read      → parent mark as read
///
/// The inbox lists are sourced from <see cref="AnnouncementDelivery"/> so
/// the contract invariant "students who transferred out before publish do
/// not receive" is enforced implicitly — no delivery row exists for them.
/// </summary>
public static class AnnouncementInboxEndpoints
{
    public const string StudentListRoute = "/api/student/announcements";
    public const string StudentReadRoute = "/api/student/announcements/{deliveryId:guid}/read";
    public const string ParentListRoute = "/api/parent/announcements/{childId:guid}";
    public const string ParentReadRoute = "/api/parent/announcements/{deliveryId:guid}/read";

    public static IEndpointRouteBuilder MapAnnouncementInbox(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(StudentListRoute, HandleStudentListAsync).WithName("ListStudentAnnouncements").WithTags("Announcements");
        routes.MapPost(StudentReadRoute, HandleStudentReadAsync).WithName("MarkStudentAnnouncementRead").WithTags("Announcements");
        routes.MapGet(ParentListRoute, HandleParentListAsync).WithName("ListParentAnnouncements").WithTags("Announcements");
        routes.MapPost(ParentReadRoute, HandleParentReadAsync).WithName("MarkParentAnnouncementRead").WithTags("Announcements");
        return routes;
    }

    public static async Task<IResult> HandleStudentListAsync(
        HttpContext http,
        IAnnouncementDeliveryRepository deliveries,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetStudentProfileId(http, out var studentId))
        {
            return Results.Unauthorized();
        }

        var rows = await deliveries.ListForRecipientAsync(tenantId, studentId, "student", ct);
        var items = await EnrichAsync(db, tenantId, rows, ct);
        return Results.Ok(new { announcements = items, total_count = items.Count });
    }

    public static async Task<IResult> HandleStudentReadAsync(
        Guid deliveryId,
        HttpContext http,
        IAnnouncementDeliveryRepository deliveries,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetStudentProfileId(http, out var studentId))
        {
            return Results.Unauthorized();
        }

        return await MarkReadAsync(tenantId, deliveryId, studentId, "student", deliveries, ct);
    }

    public static async Task<IResult> HandleParentListAsync(
        Guid childId,
        HttpContext http,
        IAnnouncementDeliveryRepository deliveries,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetParentProfileId(http, out var parentProfileId))
        {
            return Results.Unauthorized();
        }

        var today = DateTime.UtcNow.Date;
        var link = await db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId
                && l.ParentProfileId == parentProfileId
                && l.StudentId == childId
                && l.EffectiveStart <= today
                && (l.EffectiveEnd == null || l.EffectiveEnd >= today), ct);
        if (link is null) return Results.StatusCode(403);

        var rows = await deliveries.ListForRecipientAsync(tenantId, parentProfileId, "parent", ct);
        var items = await EnrichAsync(db, tenantId, rows, ct);
        return Results.Ok(new { child_id = childId, announcements = items, total_count = items.Count });
    }

    public static async Task<IResult> HandleParentReadAsync(
        Guid deliveryId,
        HttpContext http,
        IAnnouncementDeliveryRepository deliveries,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetParentProfileId(http, out var parentProfileId))
        {
            return Results.Unauthorized();
        }

        return await MarkReadAsync(tenantId, deliveryId, parentProfileId, "parent", deliveries, ct);
    }

    private static async Task<IResult> MarkReadAsync(
        Guid tenantId,
        Guid deliveryId,
        Guid recipientId,
        string recipientRole,
        IAnnouncementDeliveryRepository deliveries,
        CancellationToken ct)
    {
        var rows = await deliveries.ListForRecipientAsync(tenantId, recipientId, recipientRole, ct);
        var match = rows.FirstOrDefault(d => d.AnnouncementDeliveryId == deliveryId);
        if (match is null) return Results.NotFound(new { error = "delivery_not_found" });

        await deliveries.MarkReadAsync(tenantId, deliveryId, ct);
        await deliveries.SaveChangesAsync(ct);
        return Results.Ok(new { delivery_id = deliveryId, delivery_status = "read", read_at = DateTime.UtcNow });
    }

    private static async Task<List<object>> EnrichAsync(
        MuallimiDbContext db,
        Guid tenantId,
        IReadOnlyList<AnnouncementDelivery> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return new List<object>();
        var ids = rows.Select(r => r.AnnouncementId).Distinct().ToList();
        var announcements = await db.Announcements
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && ids.Contains(a.AnnouncementId))
            .ToDictionaryAsync(a => a.AnnouncementId, ct);

        return rows
            .Where(r => announcements.ContainsKey(r.AnnouncementId))
            .OrderByDescending(r => announcements[r.AnnouncementId].PublishedAt ?? announcements[r.AnnouncementId].CreatedAt)
            .Select(r =>
            {
                var a = announcements[r.AnnouncementId];
                return (object)new
                {
                    delivery_id = r.AnnouncementDeliveryId,
                    announcement_id = a.AnnouncementId,
                    title_ar = a.TitleAr,
                    title_en = a.TitleEn,
                    body_ar = a.BodyAr,
                    body_en = a.BodyEn,
                    published_at = a.PublishedAt,
                    delivery_status = r.DeliveryStatus,
                    delivered_at = r.DeliveredAt,
                    read_at = r.ReadAt,
                };
            })
            .ToList();
    }

    private static bool TryGetStudentProfileId(HttpContext http, out Guid studentId)
        => Guid.TryParse(http.Request.Headers[LeaderboardEndpoints.StudentHeaderName].ToString(), out studentId);

    private static bool TryGetParentProfileId(HttpContext http, out Guid parentProfileId)
        => Guid.TryParse(http.Request.Headers[LeaderboardEndpoints.ParentHeaderName].ToString(), out parentProfileId);
}
