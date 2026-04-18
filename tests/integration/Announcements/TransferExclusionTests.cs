using System;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Api.Announcements.AnnouncementCreation;
using Muallimi.Api.Announcements.AnnouncementDispatch;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Announcements;

/// <summary>
/// T156 (US8) — integration test for the transferred-student exclusion
/// invariant.
///
/// The contract says: "students who have been transferred out of a class
/// before publish time do not receive the announcement". The harness
/// seeds one transferred-out student in the target class; we publish the
/// announcement and assert they do not appear in the delivery fan-out.
/// </summary>
public class TransferExclusionTests
{
    [Fact]
    public async Task Transferred_Students_Are_Excluded_From_Delivery()
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

        var row = new Announcement
        {
            AnnouncementId = Guid.NewGuid(),
            TenantId = AnnouncementHarness.TenantAlpha,
            SchoolTenantId = AnnouncementHarness.SchoolAlpha,
            CreatedById = AnnouncementHarness.AdminAlpha,
            TargetScope = "class",
            TargetId = AnnouncementHarness.ClassAlpha7A,
            TitleAr = "إعلان",
            TitleEn = "Announcement",
            BodyAr = "…",
            BodyEn = "…",
            Status = "draft",
            CreatedAt = DateTime.UtcNow,
        };
        await repo.AddAsync(row);
        await repo.SaveChangesAsync();
        var result = await dispatcher.PublishAsync(row);

        var deliveries = await deliveryRepo.ListForAnnouncementAsync(AnnouncementHarness.TenantAlpha, row.AnnouncementId);
        Assert.DoesNotContain(deliveries, d => d.RecipientId == harness.TransferredStudent);
        Assert.DoesNotContain(channels.Dispatched, d => d.ChildId == harness.TransferredStudent);

        // The dispatcher result reports the correct recipient count: 4
        // active students + 1 linked parent.
        Assert.Equal(5, result.RecipientCount);
    }

    [Fact]
    public async Task Grade_Scope_Excludes_Transferred_Students_From_Other_Sections()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var resolver = new AnnouncementTargetResolver(db);
        var resolution = await resolver.ResolveAsync(
            AnnouncementHarness.TenantAlpha,
            AnnouncementHarness.SchoolAlpha,
            "grade",
            "7",
            DateTime.UtcNow);

        Assert.DoesNotContain(resolution.Recipients, r => r.RecipientId == harness.TransferredStudent);
    }
}
