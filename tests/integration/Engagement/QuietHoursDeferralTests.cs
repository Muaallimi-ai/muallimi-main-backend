using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Domain.Parents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T124 (US7) — Quiet-hours deferral MUST never drop a notification.
///
/// The dispatcher writes a deferred row whenever the parent's local clock is
/// inside the quiet window. The row carries
/// <c>quiet_hours_deferred_until</c>, stays in
/// <c>delivery_state = deferred</c>, and is promoted to
/// <c>dispatched</c> via <see cref="IParentNotificationDispatcher.FlushDeferredAsync"/>
/// once the window closes. These tests pin that contract.
/// </summary>
public class QuietHoursDeferralTests
{
    [Fact]
    public async Task Notification_Inside_Quiet_Window_Is_Deferred_Not_Dropped()
    {
        var harness = new ParentNotificationsTestHarness();
        var now = DateTime.UtcNow;
        var start = now.AddHours(-1).TimeOfDay;
        var end = now.AddHours(1).TimeOfDay;
        var quietJson = $"{{\"start_time\":\"{start:hh\\:mm}\",\"end_time\":\"{end:hh\\:mm}\"}}";
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync(
            timezone: "UTC",
            quietHoursJson: quietJson);

        await harness.Db.SaveChangesAsync();

        var outcome = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            TenantId: tenantId,
            ParentProfileId: parentId,
            ChildId: childId,
            NotificationKind: "weekly_report_ready",
            BodyAr: "تقرير",
            BodyEn: "Report",
            DeepLink: "/reports/x",
            CorrelationId: "corr-q-1"));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(ParentNotificationDispatchStatus.Deferred, outcome.Status);
        Assert.NotNull(outcome.DeferredUntil);

        var row = await harness.Db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(n => n.ParentNotificationId == outcome.ParentNotificationId);
        Assert.Equal("deferred", row.DeliveryState);
        Assert.NotNull(row.QuietHoursDeferredUntil);
        Assert.Null(row.DispatchedAt);

        // Stub ledger must not have seen a receipt — quiet hours defer, never drop.
        Assert.Empty(harness.Ledger.ReceiptsFor(outcome.ParentNotificationId));
    }

    [Fact]
    public async Task FlushDeferred_Promotes_Rows_After_Quiet_Window_Closes()
    {
        var harness = new ParentNotificationsTestHarness();
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Seed a profile with quiet hours already open for flushing.
        harness.Db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = parentId,
            TenantId = tenantId,
            IdentityId = Guid.NewGuid(),
            PreferredLanguage = "ar",
            Locale = "ar-SA",
            Timezone = "UTC",
            NotificationChannels = "{\"in_app\":true,\"email\":true,\"push\":true}",
            QuietHours = "{}",
            PerChildOverrides = "{}",
            ConsentState = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        harness.Db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId, ParentProfileId = parentId, StudentId = childId,
            Role = "guardian",
            EffectiveStart = DateTime.UtcNow.Date.AddDays(-10),
            CreatedAt = now, UpdatedAt = now,
        });
        // Seed a deferred row whose quiet_hours_deferred_until already elapsed.
        harness.Db.ParentNotifications.Add(new ParentNotification
        {
            ParentNotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ParentProfileId = parentId,
            ChildId = childId,
            NotificationKind = "weekly_report_ready",
            Channel = "in_app",
            Language = "ar",
            BodyAr = "تقرير مؤجل",
            BodyEn = "Deferred report",
            DeliveryState = "deferred",
            QuietHoursDeferredUntil = now.AddMinutes(-5),
            CorrelationId = "corr-flush-1",
            CreatedAt = now.AddHours(-8),
        });
        await harness.Db.SaveChangesAsync();

        await harness.Dispatcher.FlushDeferredAsync();

        var row = await harness.Db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(n => n.TenantId == tenantId && n.ParentProfileId == parentId);
        Assert.Equal("dispatched", row.DeliveryState);
        Assert.NotNull(row.DispatchedAt);
    }

    [Fact]
    public async Task Outside_Quiet_Window_Dispatches_Immediately()
    {
        var harness = new ParentNotificationsTestHarness();
        // Carve out a 1-hour quiet window 6 hours in the future so the
        // request's "now" is guaranteed to be outside. The window is still
        // non-null, which proves the dispatcher distinguishes "inside" from
        // "configured-but-outside".
        var localNow = DateTime.UtcNow;
        var startLocal = localNow.AddHours(6).TimeOfDay.ToString(@"hh\:mm");
        var endLocal = localNow.AddHours(7).TimeOfDay.ToString(@"hh\:mm");
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync(
            timezone: "UTC",
            quietHoursJson: $"{{\"start_time\":\"{startLocal}\",\"end_time\":\"{endLocal}\"}}");

        var outcome = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            TenantId: tenantId,
            ParentProfileId: parentId,
            ChildId: childId,
            NotificationKind: "weekly_report_ready",
            BodyAr: "تقرير",
            BodyEn: "Report",
            DeepLink: "/reports/x",
            CorrelationId: "corr-imm-1"));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(ParentNotificationDispatchStatus.Dispatched, outcome.Status);
        var row = await harness.Db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(n => n.ParentNotificationId == outcome.ParentNotificationId);
        Assert.Equal("dispatched", row.DeliveryState);
        Assert.NotNull(row.DispatchedAt);
        Assert.Null(row.QuietHoursDeferredUntil);
    }

}
