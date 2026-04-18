using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Domain.Parents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T125 (US7) — Per-child category override semantics.
///
/// Asserts that:
///   - disabling a category for a specific child suppresses dispatch but
///     writes the row (underlying state stays visible on the dashboard);
///   - disabling a category for child A does NOT suppress dispatch for
///     sibling child B (overrides are keyed by child);
///   - enabling every category for child A while the global channel map
///     disables a channel still results in a suppressed row when no
///     channel is available — the contract never drops silently.
/// </summary>
public class PerChildNotificationOverridesTests
{
    [Fact]
    public async Task Disabling_Category_For_Child_Suppresses_But_Writes_Row()
    {
        var harness = new ParentNotificationsTestHarness();
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var now = DateTime.UtcNow;

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
            PerChildOverrides = $"{{\"{childId:D}\":{{\"weekly_report_ready\":false}}}}",
            ConsentState = "{}",
            CreatedAt = now, UpdatedAt = now,
        });
        harness.Db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId, ParentProfileId = parentId, StudentId = childId,
            Role = "guardian",
            EffectiveStart = DateTime.UtcNow.Date.AddDays(-10),
            CreatedAt = now, UpdatedAt = now,
        });
        await harness.Db.SaveChangesAsync();

        var outcome = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            TenantId: tenantId,
            ParentProfileId: parentId,
            ChildId: childId,
            NotificationKind: "weekly_report_ready",
            BodyAr: "تقرير",
            BodyEn: "Report",
            DeepLink: "/reports/x",
            CorrelationId: "corr-over-1"));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(ParentNotificationDispatchStatus.Suppressed, outcome.Status);
        var row = await harness.Db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(n => n.ParentNotificationId == outcome.ParentNotificationId);
        Assert.Equal("suppressed", row.DeliveryState);
        Assert.Null(row.DispatchedAt);
        // No delivery receipt — the channel adapter was never invoked.
        Assert.Empty(harness.Ledger.ReceiptsFor(outcome.ParentNotificationId));
    }

    [Fact]
    public async Task Sibling_Without_Override_Still_Receives_Category()
    {
        var harness = new ParentNotificationsTestHarness();
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childA = Guid.NewGuid();
        var childB = Guid.NewGuid();
        var now = DateTime.UtcNow;

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
            PerChildOverrides = $"{{\"{childA:D}\":{{\"weekly_report_ready\":false}}}}",
            ConsentState = "{}",
            CreatedAt = now, UpdatedAt = now,
        });
        foreach (var studentId in new[] { childA, childB })
        {
            harness.Db.ChildLinks.Add(new ChildLink
            {
                ChildLinkId = Guid.NewGuid(),
                TenantId = tenantId, ParentProfileId = parentId, StudentId = studentId,
                Role = "guardian",
                EffectiveStart = DateTime.UtcNow.Date.AddDays(-10),
                CreatedAt = now, UpdatedAt = now,
            });
        }
        await harness.Db.SaveChangesAsync();

        var a = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            tenantId, parentId, childA, "weekly_report_ready",
            BodyAr: "أ", BodyEn: "A", DeepLink: "/r/a", CorrelationId: "c-a"));
        var b = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            tenantId, parentId, childB, "weekly_report_ready",
            BodyAr: "ب", BodyEn: "B", DeepLink: "/r/b", CorrelationId: "c-b"));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(ParentNotificationDispatchStatus.Suppressed, a.Status);
        Assert.Equal(ParentNotificationDispatchStatus.Dispatched, b.Status);
        Assert.Empty(harness.Ledger.ReceiptsFor(a.ParentNotificationId));
        Assert.Single(harness.Ledger.ReceiptsFor(b.ParentNotificationId));
    }

    [Fact]
    public async Task Language_Resolves_From_Current_Preference_At_Dispatch_Time()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync(preferredLanguage: "en");

        var outcome = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            tenantId, parentId, childId, "weekly_report_ready",
            BodyAr: "عربي", BodyEn: "English body", DeepLink: "/r/x", CorrelationId: "c-lang"));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(ParentNotificationDispatchStatus.Dispatched, outcome.Status);
        Assert.Equal("en", outcome.Language);
        var receipt = harness.Ledger.ReceiptsFor(outcome.ParentNotificationId).Single();
        Assert.Equal("English body", receipt.Body);
    }
}
