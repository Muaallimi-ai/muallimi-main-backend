using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Announcements.AnnouncementDispatch;
using Muallimi.Api.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Announcements.AnnouncementCreation;

/// <summary>
/// T161 (US8) — school-admin announcement endpoints.
///
/// Routes:
///   • POST /api/school-admin/announcements                                 → create draft/scheduled
///   • GET  /api/school-admin/announcements                                 → list announcements + delivery summaries
///   • POST /api/school-admin/announcements/{announcementId}/publish        → publish immediately
///   • GET  /api/school-admin/announcements/{announcementId}/delivery       → per-recipient delivery report
///
/// All routes require the school admin headers + school tenant scope. The
/// create endpoint recognises three target scopes (class, grade, school)
/// and returns a recipient estimate computed via the shared
/// <see cref="IAnnouncementTargetResolver"/>.
/// </summary>
public static class AnnouncementEndpoints
{
    public const string CreateRoute = "/api/school-admin/announcements";
    public const string ListRoute = "/api/school-admin/announcements";
    public const string PublishRoute = "/api/school-admin/announcements/{announcementId:guid}/publish";
    public const string DeliveryRoute = "/api/school-admin/announcements/{announcementId:guid}/delivery";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public sealed record CreateRequest(
        string target_scope,
        string? target_id,
        string title_ar,
        string title_en,
        string body_ar,
        string body_en,
        List<string>? attachments,
        string? scheduled_publish_at);

    public static IEndpointRouteBuilder MapAnnouncementAdmin(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(CreateRoute, HandleCreateAsync).WithName("CreateAnnouncement").WithTags("Announcements");
        routes.MapGet(ListRoute, HandleListAsync).WithName("ListAnnouncements").WithTags("Announcements");
        routes.MapPost(PublishRoute, HandlePublishAsync).WithName("PublishAnnouncement").WithTags("Announcements");
        routes.MapGet(DeliveryRoute, HandleDeliveryReportAsync).WithName("AnnouncementDeliveryReport").WithTags("Announcements");
        return routes;
    }

    public static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        CreateRequest body,
        IAnnouncementRepository announcements,
        IAnnouncementTargetResolver resolver,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out var adminId))
            return Results.Unauthorized();

        if (body is null
            || string.IsNullOrWhiteSpace(body.target_scope)
            || string.IsNullOrWhiteSpace(body.title_ar)
            || string.IsNullOrWhiteSpace(body.title_en)
            || string.IsNullOrWhiteSpace(body.body_ar)
            || string.IsNullOrWhiteSpace(body.body_en))
        {
            return Results.BadRequest(new { error = "invalid_announcement_payload" });
        }

        if (!IsValidScope(body.target_scope))
        {
            return Results.BadRequest(new { error = "invalid_target_scope" });
        }

        if (!TryResolveTarget(body.target_scope, body.target_id, out var targetId, out var targetGrade))
        {
            return Results.BadRequest(new { error = "invalid_target_id" });
        }

        DateTime? scheduledAt = null;
        if (!string.IsNullOrWhiteSpace(body.scheduled_publish_at))
        {
            if (!DateTime.TryParse(body.scheduled_publish_at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return Results.BadRequest(new { error = "invalid_scheduled_publish_at" });
            }
            scheduledAt = parsed.ToUniversalTime();
        }

        var row = new Announcement
        {
            AnnouncementId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            CreatedById = adminId,
            TargetScope = body.target_scope,
            TargetId = targetId,
            TargetGrade = targetGrade,
            TitleAr = body.title_ar,
            TitleEn = body.title_en,
            BodyAr = body.body_ar,
            BodyEn = body.body_en,
            Attachments = SerializeAttachments(body.attachments),
            ScheduledPublishAt = scheduledAt,
            PublishedAt = null,
            Status = scheduledAt is null ? "draft" : "scheduled",
            CreatedAt = DateTime.UtcNow,
        };

        await announcements.AddAsync(row, ct);
        await announcements.SaveChangesAsync(ct);

        var resolution = await resolver.ResolveAsync(
            tenantId, schoolTenantId, row.TargetScope,
            ResolveRaw(row), DateTime.UtcNow, ct);

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Created(
            uri: $"{CreateRoute}/{row.AnnouncementId}",
            value: new
            {
                announcement_id = row.AnnouncementId,
                status = row.Status,
                target_scope = row.TargetScope,
                target_id = body.target_id,
                recipient_count = resolution.Recipients.Count,
                student_count = resolution.StudentCount,
                parent_count = resolution.ParentCount,
                created_at = row.CreatedAt,
            });
    }

    public static async Task<IResult> HandleListAsync(
        HttpContext http,
        IAnnouncementRepository announcements,
        IAnnouncementDeliveryRepository deliveries,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out _))
            return Results.Unauthorized();

        var rows = await announcements.ListForSchoolAsync(tenantId, schoolTenantId, ct);
        var items = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var all = await deliveries.ListForAnnouncementAsync(tenantId, row.AnnouncementId, ct);
            items.Add(new
            {
                announcement_id = row.AnnouncementId,
                title_ar = row.TitleAr,
                title_en = row.TitleEn,
                target_scope = row.TargetScope,
                target_id = ResolveRaw(row),
                status = row.Status,
                scheduled_publish_at = row.ScheduledPublishAt,
                published_at = row.PublishedAt,
                recipient_count = all.Count,
                delivered_count = all.Count(d => d.DeliveryStatus == "delivered" || d.DeliveryStatus == "read"),
                read_count = all.Count(d => d.DeliveryStatus == "read"),
                failed_count = all.Count(d => d.DeliveryStatus == "failed"),
            });
        }
        return Results.Ok(new { announcements = items, total_count = items.Count });
    }

    public static async Task<IResult> HandlePublishAsync(
        Guid announcementId,
        HttpContext http,
        IAnnouncementRepository announcements,
        IAnnouncementDispatcher dispatcher,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out _))
            return Results.Unauthorized();

        var row = await announcements.GetByIdAsync(tenantId, schoolTenantId, announcementId, ct);
        if (row is null) return Results.NotFound(new { error = "announcement_not_found" });
        if (row.Status == "published") return Results.Conflict(new { error = "already_published" });

        var result = await dispatcher.PublishAsync(row, ct);
        return Results.Ok(new
        {
            announcement_id = result.AnnouncementId,
            status = "published",
            recipient_count = result.RecipientCount,
            delivered_count = result.DeliveredCount,
            failed_count = result.FailedCount,
            published_at = row.PublishedAt,
        });
    }

    public static async Task<IResult> HandleDeliveryReportAsync(
        Guid announcementId,
        HttpContext http,
        IAnnouncementRepository announcements,
        IAnnouncementDeliveryRepository deliveries,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out _))
            return Results.Unauthorized();

        var row = await announcements.GetByIdAsync(tenantId, schoolTenantId, announcementId, ct);
        if (row is null) return Results.NotFound(new { error = "announcement_not_found" });

        var rows = await deliveries.ListForAnnouncementAsync(tenantId, announcementId, ct);
        var studentIds = rows.Where(r => r.RecipientRole == "student").Select(r => r.RecipientId).Distinct().ToList();
        var parentIds = rows.Where(r => r.RecipientRole == "parent").Select(r => r.RecipientId).Distinct().ToList();

        var studentProfiles = await db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && studentIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        var parentProfiles = await db.ParentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && parentIds.Contains(p.ParentProfileId))
            .ToListAsync(ct);

        var items = rows.Select(r => new
        {
            delivery_id = r.AnnouncementDeliveryId,
            recipient_id = r.RecipientId,
            display_name_ar = ResolveDisplayName(r, studentProfiles),
            display_name_en = ResolveDisplayName(r, studentProfiles),
            recipient_role = r.RecipientRole,
            channel = r.Channel,
            delivery_status = r.DeliveryStatus,
            delivered_at = r.DeliveredAt,
            read_at = r.ReadAt,
        }).ToList();

        return Results.Ok(new
        {
            announcement_id = announcementId,
            deliveries = items,
            summary = new
            {
                recipient_count = rows.Count,
                delivered_count = rows.Count(r => r.DeliveryStatus == "delivered" || r.DeliveryStatus == "read"),
                read_count = rows.Count(r => r.DeliveryStatus == "read"),
                failed_count = rows.Count(r => r.DeliveryStatus == "failed"),
            },
        });
    }

    private static string ResolveDisplayName(AnnouncementDelivery delivery, IReadOnlyDictionary<Guid, string> students)
    {
        if (delivery.RecipientRole == "student" && students.TryGetValue(delivery.RecipientId, out var name))
        {
            return name;
        }
        return delivery.RecipientRole == "parent" ? "Parent" : "Recipient";
    }

    public static bool TryResolveAdmin(HttpContext http, out Guid tenantId, out Guid schoolTenantId, out Guid adminId)
    {
        tenantId = schoolTenantId = adminId = Guid.Empty;
        return SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
            && SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId)
            && SchoolManagementHeaders.TryGetSchoolAdminId(http, out adminId);
    }

    public static bool IsValidScope(string scope)
        => scope is "class" or "grade" or "school";

    public static bool TryResolveTarget(string scope, string? raw, out Guid? targetId, out int? targetGrade)
    {
        targetId = null;
        targetGrade = null;
        switch (scope)
        {
            case "class":
                if (!Guid.TryParse(raw, out var classId)) return false;
                targetId = classId;
                return true;
            case "grade":
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var grade)) return false;
                targetGrade = grade;
                return true;
            case "school":
                return true;
            default:
                return false;
        }
    }

    public static string? ResolveRaw(Announcement row) => row.TargetScope switch
    {
        "class" => row.TargetId?.ToString("D"),
        "grade" => row.TargetGrade?.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string? SerializeAttachments(List<string>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return null;
        return JsonSerializer.Serialize(attachments, JsonOptions);
    }
}
