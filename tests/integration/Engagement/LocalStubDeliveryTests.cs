using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T126 (US7) — Local-stub delivery-receipt round-trip tests.
///
/// The three local channel adapters must each:
///   - write a receipt row into the in-memory ledger keyed by
///     <c>parent_notification_id</c>;
///   - propagate the originating correlation identifier untouched;
///   - carry the deep-link metadata the dispatcher attached to the request.
///
/// Additionally, the scheduler hook must fan a single weekly-report-ready
/// signal into one notification per active linked parent (sibling + co-parent
/// shapes both covered).
/// </summary>
public class LocalStubDeliveryTests
{
    [Fact]
    public async Task In_App_Channel_Round_Trip_Captures_Receipt_With_Correlation_And_Deep_Link()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync(
            notificationChannels: "{\"in_app\":true,\"email\":false,\"push\":false}");

        var outcome = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            tenantId, parentId, childId, "weekly_report_ready",
            BodyAr: "ملخص", BodyEn: "Summary", DeepLink: "/reports/42", CorrelationId: "corr-abc"));
        await harness.Db.SaveChangesAsync();

        Assert.Equal(ParentNotificationDispatchStatus.Dispatched, outcome.Status);
        Assert.Equal("in_app", outcome.Channel);

        var receipt = harness.Ledger.ReceiptsFor(outcome.ParentNotificationId).Single();
        Assert.Equal("in_app", receipt.Channel);
        Assert.Equal("/reports/42", receipt.DeepLink);
        Assert.Equal("corr-abc", receipt.CorrelationId);
        Assert.Equal("ملخص", receipt.Body);
    }

    [Fact]
    public async Task Push_Wins_Priority_For_Urgent_Kinds_When_Enabled()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync(
            notificationChannels: "{\"in_app\":true,\"email\":true,\"push\":true}");

        var outcome = await harness.Dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
            tenantId, parentId, childId, "at_risk_flagged",
            BodyAr: null, BodyEn: null, DeepLink: "/interventions/1", CorrelationId: "c-urgent"));
        await harness.Db.SaveChangesAsync();

        Assert.Equal("push", outcome.Channel);
        var receipt = harness.Ledger.ReceiptsFor(outcome.ParentNotificationId).Single();
        Assert.Equal("push", receipt.Channel);
    }

    [Fact]
    public async Task Scheduler_Hook_Fans_Weekly_Report_To_Every_Active_Parent()
    {
        var harness = new ParentNotificationsTestHarness();
        var tenantId = Guid.NewGuid();
        var guardian = Guid.NewGuid();
        var coParent = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        foreach (var profileId in new[] { guardian, coParent })
        {
            harness.Db.ParentProfiles.Add(new ParentProfile
            {
                ParentProfileId = profileId,
                TenantId = tenantId,
                IdentityId = Guid.NewGuid(),
                PreferredLanguage = "ar",
                Locale = "ar-SA",
                Timezone = "UTC",
                NotificationChannels = "{\"in_app\":true,\"email\":true,\"push\":true}",
                QuietHours = "{}",
                PerChildOverrides = "{}",
                ConsentState = "{}",
                CreatedAt = now, UpdatedAt = now,
            });
            harness.Db.ChildLinks.Add(new ChildLink
            {
                ChildLinkId = Guid.NewGuid(),
                TenantId = tenantId, ParentProfileId = profileId, StudentId = childId,
                Role = profileId == guardian ? "guardian" : "co_parent",
                EffectiveStart = DateTime.UtcNow.Date.AddDays(-10),
                CreatedAt = now, UpdatedAt = now,
            });
        }

        var report = new WeeklyReport
        {
            WeeklyReportId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = childId,
            WindowStart = DateTime.UtcNow.Date.AddDays(-7),
            WindowEnd = DateTime.UtcNow.Date.AddDays(-1),
            GeneratedAt = DateTime.UtcNow,
            RunId = Guid.NewGuid(),
            MasteryDeltas = "[]",
            TopFocusAreas = "[]",
            AwardedBadges = "[]",
            SummaryAr = "ملخص",
            SummaryEn = "Summary",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            EvidenceRefs = "[\"ev-1\",\"ev-2\"]",
            Status = "ready",
            CorrelationId = "corr-weekly-7",
        };
        harness.Db.WeeklyReports.Add(report);
        await harness.Db.SaveChangesAsync();

        var outcomes = await harness.SchedulerHook.OnWeeklyReportReadyAsync(report);
        await harness.Db.SaveChangesAsync();

        Assert.Equal(2, outcomes.Count);
        var rows = await harness.Db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.ChildId == childId)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("weekly_report_ready", r.NotificationKind));
        Assert.All(rows, r => Assert.Equal("corr-weekly-7", r.CorrelationId));
    }

    [Fact]
    public async Task Inactive_Window_Maps_To_Weekly_Window_Inactive_Kind()
    {
        var harness = new ParentNotificationsTestHarness();
        var (tenantId, parentId, childId) = await harness.SeedParentAndChildAsync();

        var report = new WeeklyReport
        {
            WeeklyReportId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = childId,
            WindowStart = DateTime.UtcNow.Date.AddDays(-7),
            WindowEnd = DateTime.UtcNow.Date.AddDays(-1),
            GeneratedAt = DateTime.UtcNow,
            RunId = Guid.NewGuid(),
            MasteryDeltas = "[]",
            TopFocusAreas = "[]",
            AwardedBadges = "[]",
            SummaryAr = "ملخص",
            SummaryEn = "Summary",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            EvidenceRefs = "[]",
            Status = "ready",
            CorrelationId = "corr-inactive",
        };
        harness.Db.WeeklyReports.Add(report);
        await harness.Db.SaveChangesAsync();

        await harness.SchedulerHook.OnWeeklyReportReadyAsync(report);
        await harness.Db.SaveChangesAsync();

        var row = await harness.Db.ParentNotifications
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(n => n.TenantId == tenantId && n.ParentProfileId == parentId);
        Assert.Equal("weekly_window_inactive", row.NotificationKind);
    }
}
