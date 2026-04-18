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
/// T153 (US8) — contract test for the announcement CRUD + publish surface.
///
/// Pins route constants, asserts scope validation, and exercises the
/// repository + dispatcher pipeline end-to-end so the endpoint handlers
/// have a deterministic contract to bind to. Parent fan-out and dispatch
/// metadata are asserted here to keep the CRUD story self-contained.
/// </summary>
public class AnnouncementCrudTests
{
    [Fact]
    public void Route_Constants_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/announcements", AnnouncementEndpoints.CreateRoute);
        Assert.Equal("/api/school-admin/announcements", AnnouncementEndpoints.ListRoute);
        Assert.Equal("/api/school-admin/announcements/{announcementId:guid}/publish", AnnouncementEndpoints.PublishRoute);
        Assert.Equal("/api/school-admin/announcements/{announcementId:guid}/delivery", AnnouncementEndpoints.DeliveryRoute);
    }

    [Theory]
    [InlineData("class", true)]
    [InlineData("grade", true)]
    [InlineData("school", true)]
    [InlineData("teacher", false)]
    [InlineData("", false)]
    public void Scope_Validation_Is_Pinned(string scope, bool expected)
    {
        Assert.Equal(expected, AnnouncementEndpoints.IsValidScope(scope));
    }

    [Theory]
    [InlineData("class", "not-a-guid", false)]
    [InlineData("grade", "seven", false)]
    [InlineData("grade", "7", true)]
    [InlineData("school", null, true)]
    public void Target_Parsing_Respects_Scope(string scope, string? raw, bool expected)
    {
        var parsed = AnnouncementEndpoints.TryResolveTarget(scope, raw, out _, out _);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public async Task Publish_Pipeline_Creates_Delivery_Rows_And_Outbox_Event()
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
            TargetScope = "class",
            TargetId = AnnouncementHarness.ClassAlpha7A,
            TitleAr = "إعلان",
            TitleEn = "Announcement",
            BodyAr = "مرحبا",
            BodyEn = "Hello",
            Status = "draft",
            CreatedAt = DateTime.UtcNow,
        };
        await repo.AddAsync(announcement);
        await repo.SaveChangesAsync();

        var result = await dispatcher.PublishAsync(announcement);

        // 4 active Alpha 7A students + 1 parent (linked to student 1) = 5 recipients.
        Assert.Equal(5, result.RecipientCount);
        Assert.Equal(5, result.DeliveredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal("published", announcement.Status);
        Assert.NotNull(announcement.PublishedAt);

        var deliveries = await deliveryRepo.ListForAnnouncementAsync(AnnouncementHarness.TenantAlpha, announcement.AnnouncementId);
        Assert.Equal(5, deliveries.Count);
        Assert.DoesNotContain(deliveries, d => d.RecipientId == harness.TransferredStudent);
        Assert.All(deliveries, d => Assert.Equal("in_app", d.Channel));
        Assert.All(deliveries, d => Assert.Equal("delivered", d.DeliveryStatus));

        // Outbox event was enqueued with the announcement_sent kind.
        var outboxRows = db.Phase5DownstreamEvents.Local.ToList();
        Assert.Single(outboxRows);
        Assert.Equal(nameof(Phase5DownstreamEventKind.announcement_sent), outboxRows[0].EventKind);
        Assert.Equal(AnnouncementHarness.TenantAlpha, outboxRows[0].TenantId);
        Assert.Equal(AnnouncementHarness.SchoolAlpha, outboxRows[0].SchoolTenantId);

        // Channel dispatch fan-out includes parent + every active student.
        Assert.Equal(5, channels.Dispatched.Count);
        Assert.Contains(channels.Dispatched, r => r.Metadata["recipient_role"] == "parent");
    }

    [Fact]
    public async Task Scheduled_Announcements_Stay_In_Scheduled_State_Until_Scheduler_Publishes()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var repo = new AnnouncementRepository(db);
        var announcement = new Announcement
        {
            AnnouncementId = Guid.NewGuid(),
            TenantId = AnnouncementHarness.TenantAlpha,
            SchoolTenantId = AnnouncementHarness.SchoolAlpha,
            CreatedById = AnnouncementHarness.AdminAlpha,
            TargetScope = "school",
            TargetId = null,
            TitleAr = "إعلان",
            TitleEn = "Announcement",
            BodyAr = "…",
            BodyEn = "…",
            ScheduledPublishAt = DateTime.UtcNow.AddMinutes(-1),
            Status = "scheduled",
            CreatedAt = DateTime.UtcNow,
        };
        await repo.AddAsync(announcement);
        await repo.SaveChangesAsync();

        var due = await repo.ListScheduledDueAsync(DateTime.UtcNow);
        Assert.Contains(due, d => d.AnnouncementId == announcement.AnnouncementId);
    }
}
