using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Announcements.AnnouncementCreation;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Announcements.AnnouncementDispatch;

/// <summary>
/// T159 + T163 (US8) — AnnouncementDispatcher.
///
/// Runs publish time side effects inside the same unit of work:
///   1. Resolve recipients via <see cref="IAnnouncementTargetResolver"/>.
///   2. Stamp the announcement with <c>published_at</c> + status "published".
///   3. Insert an <see cref="AnnouncementDelivery"/> row per recipient and
///      dispatch through the Phase 4 <see cref="INotificationChannelAdapterRegistry"/>
///      so the local in_app stub records receipts and the production
///      adapters bind the same interface.
///   4. Enqueue a <see cref="Phase5DownstreamEventKind.announcement_sent"/>
///      outbox row so downstream consumers (Phase 6 ops dashboards, school
///      report aggregator) see the event.
///
/// Failures on individual channel dispatches are captured as
/// <c>delivery_status="failed"</c>; the announcement still publishes so
/// partial fan-out is observable in the delivery report.
/// </summary>
public interface IAnnouncementDispatcher
{
    Task<AnnouncementDispatchResult> PublishAsync(Announcement row, CancellationToken ct = default);
}

public sealed record AnnouncementDispatchResult(
    Guid AnnouncementId,
    int RecipientCount,
    int DeliveredCount,
    int FailedCount);

public sealed class AnnouncementDispatcher : IAnnouncementDispatcher
{
    private readonly MuallimiDbContext _db;
    private readonly IAnnouncementTargetResolver _resolver;
    private readonly IAnnouncementDeliveryRepository _deliveries;
    private readonly INotificationChannelAdapterRegistry _channels;
    private readonly IPhase5DownstreamEventOutbox _outbox;

    public AnnouncementDispatcher(
        MuallimiDbContext db,
        IAnnouncementTargetResolver resolver,
        IAnnouncementDeliveryRepository deliveries,
        INotificationChannelAdapterRegistry channels,
        IPhase5DownstreamEventOutbox outbox)
    {
        _db = db;
        _resolver = resolver;
        _deliveries = deliveries;
        _channels = channels;
        _outbox = outbox;
    }

    public async Task<AnnouncementDispatchResult> PublishAsync(Announcement row, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var resolution = await _resolver.ResolveAsync(
            row.TenantId,
            row.SchoolTenantId,
            row.TargetScope,
            ResolveRaw(row),
            now,
            ct);

        row.PublishedAt = now;
        row.Status = "published";

        var correlationId = Guid.NewGuid().ToString("D");
        var deliveries = new List<AnnouncementDelivery>(resolution.Recipients.Count);
        var delivered = 0;
        var failed = 0;

        foreach (var recipient in resolution.Recipients)
        {
            var channel = "in_app";
            var delivery = new AnnouncementDelivery
            {
                AnnouncementDeliveryId = Guid.NewGuid(),
                TenantId = row.TenantId,
                AnnouncementId = row.AnnouncementId,
                RecipientId = recipient.RecipientId,
                RecipientRole = recipient.RecipientRole,
                Channel = channel,
                DeliveryStatus = "queued",
                CorrelationId = correlationId,
            };
            deliveries.Add(delivery);

            try
            {
                var adapter = _channels.Get(channel);
                await adapter.DispatchAsync(new NotificationDispatchRequest(
                    TenantId: row.TenantId,
                    ParentProfileId: recipient.RecipientRole == "parent" ? recipient.RecipientId : Guid.Empty,
                    ChildId: recipient.RecipientRole == "student" ? recipient.RecipientId : Guid.Empty,
                    NotificationKind: "announcement",
                    Language: "ar",
                    Title: row.TitleAr,
                    Body: row.BodyAr,
                    Metadata: new Dictionary<string, string>
                    {
                        ["announcement_id"] = row.AnnouncementId.ToString("D"),
                        ["school_tenant_id"] = row.SchoolTenantId.ToString("D"),
                        ["target_scope"] = row.TargetScope,
                        ["recipient_role"] = recipient.RecipientRole,
                    },
                    CorrelationId: correlationId), ct);
                delivery.DeliveryStatus = "delivered";
                delivery.DeliveredAt = now;
                delivered++;
            }
            catch
            {
                delivery.DeliveryStatus = "failed";
                failed++;
            }
        }

        await _deliveries.AddRangeAsync(deliveries, ct);

        await _outbox.EnqueueAsync(
            Phase5DownstreamEventKind.announcement_sent,
            row.TenantId,
            row.SchoolTenantId,
            payload: new
            {
                announcement_id = row.AnnouncementId,
                target_scope = row.TargetScope,
                target_class_id = row.TargetId,
                target_grade = row.TargetGrade,
                recipient_count = resolution.Recipients.Count,
                student_count = resolution.StudentCount,
                parent_count = resolution.ParentCount,
                published_at = now,
            },
            correlationId: correlationId,
            occurredAt: now,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        return new AnnouncementDispatchResult(
            AnnouncementId: row.AnnouncementId,
            RecipientCount: resolution.Recipients.Count,
            DeliveredCount: delivered,
            FailedCount: failed);
    }

    private static string? ResolveRaw(Announcement row)
    {
        return row.TargetScope switch
        {
            "class" => row.TargetId?.ToString("D"),
            "grade" => row.TargetGrade?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}

public static class AnnouncementDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5AnnouncementDispatcher(this IServiceCollection services)
    {
        services.AddScoped<IAnnouncementDispatcher, AnnouncementDispatcher>();
        return services;
    }
}
