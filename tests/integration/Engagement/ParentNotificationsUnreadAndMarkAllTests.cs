using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Parents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// Covers the unread-count + mark-all-read repository surface that backs
/// <c>GET /api/parent/notifications/unread-count</c> and
/// <c>POST /api/parent/notifications/mark-all-read</c>.
///
/// Both are scoped by the parent's active <see cref="ChildLink"/> set, mirror
/// the inbox listing's tenant + parent gating, and only touch rows whose
/// <c>delivery_state</c> is <c>"dispatched"</c> or <c>"deferred"</c> — the
/// terminal states (<c>"read"</c>, <c>"suppressed"</c>, <c>"failed"</c>) stay
/// untouched so a "mark all read" never accidentally resurrects a suppressed
/// row or tampers with audit trail evidence.
/// </summary>
public class ParentNotificationsUnreadAndMarkAllTests
{
    [Fact]
    public async Task UnreadCount_Counts_Only_Dispatched_And_Deferred_For_Parent()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync();
        var now = DateTime.UtcNow;

        // 2 dispatched + 1 deferred + 1 read + 1 suppressed → unread = 3.
        SeedRow(harness, tenantId, parentId, childId, "dispatched", now);
        SeedRow(harness, tenantId, parentId, childId, "dispatched", now);
        SeedRow(harness, tenantId, parentId, childId, "deferred", now);
        SeedRow(harness, tenantId, parentId, childId, "read", now);
        SeedRow(harness, tenantId, parentId, childId, "suppressed", now);
        await harness.Db.SaveChangesAsync();

        var unread = await harness.Notifications.CountUnreadForParentAsync(
            tenantId, parentId, new[] { childId });

        Assert.Equal(3, unread);
    }

    [Fact]
    public async Task UnreadCount_Excludes_Children_Outside_Active_Link_Set()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync();
        var orphanChildId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // 1 unread for the linked child, 2 unread for an orphan child id
        // that the parent has NO active link to. Endpoint passes only the
        // active-link set, so the orphan rows must be invisible.
        SeedRow(harness, tenantId, parentId, childId, "dispatched", now);
        SeedRow(harness, tenantId, parentId, orphanChildId, "dispatched", now);
        SeedRow(harness, tenantId, parentId, orphanChildId, "deferred", now);
        await harness.Db.SaveChangesAsync();

        var unread = await harness.Notifications.CountUnreadForParentAsync(
            tenantId, parentId, new[] { childId });

        Assert.Equal(1, unread);
    }

    [Fact]
    public async Task UnreadCount_Returns_Zero_When_Parent_Has_No_Active_Children()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, _) = await harness.SeedParentAndChildAsync();

        var unread = await harness.Notifications.CountUnreadForParentAsync(
            tenantId, parentId, Array.Empty<Guid>());

        Assert.Equal(0, unread);
    }

    [Fact]
    public async Task MarkAllAsRead_Flips_Dispatched_And_Deferred_Only()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync();
        var now = DateTime.UtcNow;

        var dispatched = SeedRow(harness, tenantId, parentId, childId, "dispatched", now);
        var deferred = SeedRow(harness, tenantId, parentId, childId, "deferred", now);
        var alreadyRead = SeedRow(harness, tenantId, parentId, childId, "read", now);
        var suppressed = SeedRow(harness, tenantId, parentId, childId, "suppressed", now);
        await harness.Db.SaveChangesAsync();

        var marked = await harness.Notifications.MarkAllAsReadForParentAsync(
            tenantId, parentId, new[] { childId });
        await harness.Db.SaveChangesAsync();

        Assert.Equal(2, marked);
        Assert.Equal("read", await StateOf(harness, dispatched));
        Assert.Equal("read", await StateOf(harness, deferred));
        // Terminal states untouched — mark-all is non-destructive.
        Assert.Equal("read", await StateOf(harness, alreadyRead));
        Assert.Equal("suppressed", await StateOf(harness, suppressed));
    }

    [Fact]
    public async Task MarkAllAsRead_Is_Tenant_And_Parent_Scoped()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantA, parentA, childA) = await harness.SeedParentAndChildAsync();
        var (tenantB, parentB, childB) = await harness.SeedParentAndChildAsync();
        var now = DateTime.UtcNow;

        var aRow = SeedRow(harness, tenantA, parentA, childA, "dispatched", now);
        var bRow = SeedRow(harness, tenantB, parentB, childB, "dispatched", now);
        await harness.Db.SaveChangesAsync();

        var marked = await harness.Notifications.MarkAllAsReadForParentAsync(
            tenantA, parentA, new[] { childA });
        await harness.Db.SaveChangesAsync();

        Assert.Equal(1, marked);
        Assert.Equal("read", await StateOf(harness, aRow));
        // Different tenant + parent — must stay unread even though state matches.
        Assert.Equal("dispatched", await StateOf(harness, bRow));
    }

    [Fact]
    public async Task UnreadCount_Drops_To_Zero_After_MarkAllAsRead()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync();
        var now = DateTime.UtcNow;

        SeedRow(harness, tenantId, parentId, childId, "dispatched", now);
        SeedRow(harness, tenantId, parentId, childId, "deferred", now);
        SeedRow(harness, tenantId, parentId, childId, "dispatched", now);
        await harness.Db.SaveChangesAsync();

        Assert.Equal(3, await harness.Notifications.CountUnreadForParentAsync(
            tenantId, parentId, new[] { childId }));

        await harness.Notifications.MarkAllAsReadForParentAsync(
            tenantId, parentId, new[] { childId });
        await harness.Db.SaveChangesAsync();

        Assert.Equal(0, await harness.Notifications.CountUnreadForParentAsync(
            tenantId, parentId, new[] { childId }));
    }

    private static Guid SeedRow(
        ParentNotificationsTestHarness harness,
        Guid tenantId,
        Guid parentId,
        Guid childId,
        string deliveryState,
        DateTime now)
    {
        var id = Guid.NewGuid();
        harness.Db.ParentNotifications.Add(new ParentNotification
        {
            ParentNotificationId = id,
            TenantId = tenantId,
            ParentProfileId = parentId,
            ChildId = childId,
            NotificationKind = "weekly_report_ready",
            Channel = "in_app",
            Language = "ar",
            BodyAr = "ب",
            BodyEn = "B",
            DeliveryState = deliveryState,
            CorrelationId = Guid.NewGuid().ToString("D"),
            CreatedAt = now,
        });
        return id;
    }

    private static async Task<string> StateOf(
        ParentNotificationsTestHarness harness,
        Guid parentNotificationId)
    {
        var row = await harness.Db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(n => n.ParentNotificationId == parentNotificationId);
        return row.DeliveryState;
    }
}
