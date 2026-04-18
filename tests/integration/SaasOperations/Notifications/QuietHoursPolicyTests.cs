using Muallimi.Api.Notifications;
using Muallimi.Api.Parents.ParentNotifications;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Notifications;

public class QuietHoursPolicyTests
{
    [Theory]
    [InlineData("billing.payment_failed", true)]
    [InlineData("billing.grace_period_started", true)]
    [InlineData("billing.subscription_expired", true)]
    [InlineData("incident.opened", true)]
    [InlineData("engagement.weekly_report_ready", false)]
    [InlineData("billing.payment_succeeded", false)]
    public void CriticalCategories_bypass_quiet_hours(string kind, bool expectedCritical)
    {
        Assert.Equal(expectedCritical, QuietHoursPolicy.IsCritical(kind));
    }

    [Fact]
    public void ResolveDeferral_returns_null_for_critical_category_inside_window()
    {
        var preferences = new ParentNotificationPreferences
        {
            Channels = new Dictionary<string, bool> { ["in_app"] = true },
            QuietHoursStart = TimeSpan.FromHours(22),
            QuietHoursEnd = TimeSpan.FromHours(7),
        };
        // 23:00 UTC is well inside the 22→07 quiet window.
        var insideWindow = DateTime.SpecifyKind(new DateTime(2026, 4, 18, 23, 0, 0), DateTimeKind.Utc);

        var deferral = QuietHoursPolicy.ResolveDeferral(preferences, "UTC", insideWindow, "billing.payment_failed");

        Assert.Null(deferral);
    }

    [Fact]
    public void ResolveDeferral_defers_non_critical_inside_window()
    {
        var preferences = new ParentNotificationPreferences
        {
            Channels = new Dictionary<string, bool> { ["in_app"] = true },
            QuietHoursStart = TimeSpan.FromHours(22),
            QuietHoursEnd = TimeSpan.FromHours(7),
        };
        var insideWindow = DateTime.SpecifyKind(new DateTime(2026, 4, 18, 23, 0, 0), DateTimeKind.Utc);

        var deferral = QuietHoursPolicy.ResolveDeferral(preferences, "UTC", insideWindow, "engagement.weekly_report_ready");

        Assert.NotNull(deferral);
        Assert.True(deferral!.Value > insideWindow);
    }
}
