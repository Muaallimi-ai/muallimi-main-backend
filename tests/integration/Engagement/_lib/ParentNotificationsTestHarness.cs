using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Api.Parents.ParentNotifications.Channels;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// Shared harness for US7 integration tests. Wires the Phase 4
/// notification dispatcher against an in-memory DbContext and the three
/// local-stub channel adapters so tests can drive end-to-end dispatch and
/// read back delivery receipts via <see cref="INotificationChannelStubLedger"/>.
/// </summary>
internal sealed class ParentNotificationsTestHarness
{
    public MuallimiDbContext Db { get; }
    public IParentNotificationRepository Notifications { get; }
    public IParentProfileRepository Profiles { get; }
    public IChildLinkRepository ChildLinks { get; }
    public NotificationChannelStubLedger Ledger { get; }
    public INotificationChannelAdapterRegistry ChannelRegistry { get; }
    public ParentNotificationDispatcher Dispatcher { get; }
    public NotificationSchedulerHook SchedulerHook { get; }

    public ParentNotificationsTestHarness(MuallimiDbContext? db = null)
    {
        Db = db ?? Phase4TestDbContextFactory.Create();
        Notifications = new ParentNotificationRepository(Db);
        Profiles = new ParentProfileRepository(Db);
        ChildLinks = new ChildLinkRepository(Db);
        Ledger = new NotificationChannelStubLedger();
        ChannelRegistry = new NotificationChannelAdapterRegistry(new INotificationChannelAdapter[]
        {
            TestChannelAdapter.InApp(Ledger),
            TestChannelAdapter.Email(Ledger),
            TestChannelAdapter.Push(Ledger),
        });
        Dispatcher = new ParentNotificationDispatcher(
            Db, Notifications, Profiles, ChannelRegistry,
            NullLogger<ParentNotificationDispatcher>.Instance);
        SchedulerHook = new NotificationSchedulerHook(
            Db, ChildLinks, Dispatcher,
            NullLogger<NotificationSchedulerHook>.Instance);
    }

    public async Task<(Guid tenantId, Guid parentProfileId, Guid childId)> SeedParentAndChildAsync(
        string preferredLanguage = "ar",
        string timezone = "Asia/Dubai",
        string notificationChannels = "{\"in_app\":true,\"email\":true,\"push\":true}",
        string quietHoursJson = "{}",
        string perChildOverrides = "{}")
    {
        var tenantId = Guid.NewGuid();
        var parentProfileId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        Db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = parentProfileId,
            TenantId = tenantId,
            IdentityId = Guid.NewGuid(),
            PreferredLanguage = preferredLanguage,
            Locale = preferredLanguage == "en" ? "en-US" : "ar-SA",
            Timezone = timezone,
            NotificationChannels = notificationChannels,
            QuietHours = quietHoursJson,
            PerChildOverrides = perChildOverrides,
            ConsentState = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        Db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId,
            ParentProfileId = parentProfileId,
            StudentId = childId,
            Role = "guardian",
            EffectiveStart = DateTime.UtcNow.Date.AddDays(-30),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync();
        return (tenantId, parentProfileId, childId);
    }
}

internal sealed class TestChannelAdapter : INotificationChannelAdapter
{
    private readonly NotificationChannelStubLedger _ledger;
    private TestChannelAdapter(NotificationChannelStubLedger ledger, string channel)
    {
        _ledger = ledger;
        Channel = channel;
    }
    public string Channel { get; }
    public static TestChannelAdapter InApp(NotificationChannelStubLedger l) => new(l, "in_app");
    public static TestChannelAdapter Email(NotificationChannelStubLedger l) => new(l, "email");
    public static TestChannelAdapter Push(NotificationChannelStubLedger l) => new(l, "push");

    public Task<NotificationDispatchReceipt> DispatchAsync(
        NotificationDispatchRequest request,
        System.Threading.CancellationToken ct = default)
    {
        var receiptId = Guid.NewGuid().ToString("D");
        request.Metadata.TryGetValue("parent_notification_id", out var nidRaw);
        request.Metadata.TryGetValue("deep_link", out var deepLink);
        Guid.TryParse(nidRaw, out var parentNotificationId);
        _ledger.Record(new NotificationDispatchStubReceipt(
            ParentNotificationId: parentNotificationId,
            TenantId: request.TenantId,
            ParentProfileId: request.ParentProfileId,
            ChildId: request.ChildId,
            Channel: Channel,
            Language: request.Language,
            Body: request.Body,
            DeepLink: deepLink ?? string.Empty,
            ReceiptId: receiptId,
            DispatchedAt: DateTime.UtcNow,
            CorrelationId: request.CorrelationId));
        return Task.FromResult(new NotificationDispatchReceipt(receiptId, Channel));
    }
}
