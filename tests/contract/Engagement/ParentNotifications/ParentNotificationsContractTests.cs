using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Api.Parents.ParentNotifications.Channels;
using Muallimi.Api.Parents.ParentPreferences;
using Muallimi.Domain.Parents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Engagement.ParentNotifications;

/// <summary>
/// T123 (US7) — Contract tests for <c>phase4.parent.notifications</c>.
///
/// Pins the endpoint routes, the <see cref="ParentNotification"/> schema
/// (inbox response shape), the <see cref="ParentPreferencesRequest"/> shape
/// (PUT body), and the operator-impersonation surface constant so the
/// frontend preferences UI and the downstream job wiring stay stable when
/// main-backend refactors the underlying storage.
/// </summary>
public class ParentNotificationsContractTests
{
    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/parent/notifications", ParentNotificationsInboxEndpoint.Route);
        Assert.Equal(
            "/api/parent/notifications/unread-count",
            ParentNotificationsInboxEndpoint.UnreadCountRoute);
        Assert.Equal(
            "/api/parent/notifications/{notificationId:guid}/mark-read",
            ParentNotificationsInboxEndpoint.MarkReadRoute);
        Assert.Equal(
            "/api/parent/notifications/mark-all-read",
            ParentNotificationsInboxEndpoint.MarkAllReadRoute);
        Assert.Equal("/api/parent/notifications/preferences", ParentPreferencesEndpoint.Route);
    }

    [Fact]
    public void ParentNotification_Row_Carries_All_Contract_Fields()
    {
        var props = PropertyNamesOf<ParentNotification>();
        Assert.Contains("ParentNotificationId", props);
        Assert.Contains("TenantId", props);
        Assert.Contains("ParentProfileId", props);
        Assert.Contains("ChildId", props);
        Assert.Contains("NotificationKind", props);
        Assert.Contains("Channel", props);
        Assert.Contains("Language", props);
        Assert.Contains("BodyAr", props);
        Assert.Contains("BodyEn", props);
        Assert.Contains("QuietHoursDeferredUntil", props);
        Assert.Contains("DispatchedAt", props);
        Assert.Contains("DeliveryState", props);
        Assert.Contains("CorrelationId", props);
    }

    [Fact]
    public void Preferences_Request_Mirrors_Contract_Fields()
    {
        var props = PropertyNamesOf<ParentPreferencesRequest>();
        Assert.Contains("PreferredLanguage", props);
        Assert.Contains("Locale", props);
        Assert.Contains("Timezone", props);
        Assert.Contains("NotificationChannels", props);
        Assert.Contains("QuietHours", props);
        Assert.Contains("PerChildOverrides", props);

        var channels = PropertyNamesOf<NotificationChannelsInput>();
        Assert.Contains("InApp", channels);
        Assert.Contains("Email", channels);
        Assert.Contains("Push", channels);

        var quiet = PropertyNamesOf<QuietHoursInput>();
        Assert.Contains("StartTime", quiet);
        Assert.Contains("EndTime", quiet);

        var categories = PropertyNamesOf<NotificationCategoriesInput>();
        Assert.Contains("WeeklyReportReady", categories);
        Assert.Contains("MasteryMilestone", categories);
        Assert.Contains("FocusAreaCritical", categories);
        Assert.Contains("AtRiskFlagged", categories);
        Assert.Contains("WeeklyWindowInactive", categories);
    }

    [Fact]
    public void Dispatch_Status_Includes_Dispatched_Deferred_Suppressed_Failed()
    {
        var names = Enum.GetNames(typeof(ParentNotificationDispatchStatus)).ToHashSet();
        Assert.Contains("Dispatched", names);
        Assert.Contains("Deferred", names);
        Assert.Contains("Suppressed", names);
        Assert.Contains("Failed", names);
    }

    [Fact]
    public void Impersonation_Surface_Includes_Parent_Notifications()
    {
        Assert.Equal("parent_notifications", OperatorImpersonationSurfaces.ParentNotifications);
        Assert.Equal("preferences", OperatorImpersonationSurfaces.Preferences);
    }

    [Fact]
    public void Local_Stub_Ledger_Starts_Empty_And_Captures_Receipts()
    {
        var ledger = new NotificationChannelStubLedger();
        Assert.Empty(ledger.ReceiptsFor(Guid.NewGuid()));
        var id = Guid.NewGuid();
        ledger.Record(new NotificationDispatchStubReceipt(
            ParentNotificationId: id,
            TenantId: Guid.NewGuid(),
            ParentProfileId: Guid.NewGuid(),
            ChildId: Guid.NewGuid(),
            Channel: "in_app",
            Language: "ar",
            Body: "ب",
            DeepLink: "/x",
            ReceiptId: "r-1",
            DispatchedAt: DateTime.UtcNow,
            CorrelationId: "c-1"));
        Assert.Single(ledger.ReceiptsFor(id));
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
