using System;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Api.Announcements.AnnouncementCreation;
using Muallimi.Api.Announcements.AnnouncementDispatch;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Announcements;

/// <summary>
/// T154 (US8) — contract test for the per-recipient delivery report.
///
/// Pins the status taxonomy (queued, delivered, read, failed) and the
/// counting that the admin list endpoint exposes. The test publishes an
/// announcement, marks one recipient as read, and asserts the delivery
/// counts agree with the report projection.
/// </summary>
public class DeliveryReportTests
{
    [Fact]
    public async Task Report_Counts_Match_Status_Transitions()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var repo = new AnnouncementRepository(db);
        var deliveryRepo = new AnnouncementDeliveryRepository(db);
        var resolver = new AnnouncementTargetResolver(db);
        var channels = new RecordingNotificationChannelRegistry();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var dispatcher = new AnnouncementDispatcher(db, resolver, deliveryRepo, channels, outbox);

        var announcement = new Announcement
        {
            AnnouncementId = Guid.NewGuid(),
            TenantId = AnnouncementHarness.TenantAlpha,
            SchoolTenantId = AnnouncementHarness.SchoolAlpha,
            CreatedById = AnnouncementHarness.AdminAlpha,
            TargetScope = "grade",
            TargetGrade = 7,
            TitleAr = "إعلان",
            TitleEn = "Announcement",
            BodyAr = "…",
            BodyEn = "…",
            Status = "draft",
            CreatedAt = DateTime.UtcNow,
        };
        await repo.AddAsync(announcement);
        await repo.SaveChangesAsync();
        await dispatcher.PublishAsync(announcement);

        var all = await deliveryRepo.ListForAnnouncementAsync(AnnouncementHarness.TenantAlpha, announcement.AnnouncementId);
        Assert.Equal(8, all.Count); // 4 Alpha7A + 3 Alpha7B + 1 parent.
        Assert.All(all, d => Assert.Equal("delivered", d.DeliveryStatus));
        Assert.Equal(0, await deliveryRepo.CountByStatusAsync(AnnouncementHarness.TenantAlpha, announcement.AnnouncementId, "read"));

        var pick = all.First();
        await deliveryRepo.MarkReadAsync(AnnouncementHarness.TenantAlpha, pick.AnnouncementDeliveryId);
        await deliveryRepo.SaveChangesAsync();

        Assert.Equal(1, await deliveryRepo.CountByStatusAsync(AnnouncementHarness.TenantAlpha, announcement.AnnouncementId, "read"));
        Assert.Equal(7, await deliveryRepo.CountByStatusAsync(AnnouncementHarness.TenantAlpha, announcement.AnnouncementId, "delivered"));
    }

    [Fact]
    public async Task Cross_Tenant_Reports_Are_Isolated()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var repo = new AnnouncementRepository(db);
        var deliveryRepo = new AnnouncementDeliveryRepository(db);
        var resolver = new AnnouncementTargetResolver(db);
        var channels = new RecordingNotificationChannelRegistry();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var dispatcher = new AnnouncementDispatcher(db, resolver, deliveryRepo, channels, outbox);

        var alphaRow = new Announcement
        {
            AnnouncementId = Guid.NewGuid(),
            TenantId = AnnouncementHarness.TenantAlpha,
            SchoolTenantId = AnnouncementHarness.SchoolAlpha,
            CreatedById = AnnouncementHarness.AdminAlpha,
            TargetScope = "school",
            TitleAr = "إعلان",
            TitleEn = "Announcement",
            BodyAr = "…",
            BodyEn = "…",
            Status = "draft",
            CreatedAt = DateTime.UtcNow,
        };
        await repo.AddAsync(alphaRow);
        await repo.SaveChangesAsync();
        await dispatcher.PublishAsync(alphaRow);

        // Querying the Beta tenant returns zero rows for the Alpha announcement.
        var fromBeta = await deliveryRepo.ListForAnnouncementAsync(AnnouncementHarness.TenantBeta, alphaRow.AnnouncementId);
        Assert.Empty(fromBeta);
    }
}
