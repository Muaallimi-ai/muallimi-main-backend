using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Notifications.DeliveryTracking;
using Muallimi.Api.Notifications.RetryAndDeadLetter;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Notifications;

public class NotificationRetryServiceTests
{
    [Fact]
    public void RetrySchedule_matches_provider_contract()
    {
        Assert.Equal(new[]
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),
        }, NotificationRetryService.RetryIntervals);
        Assert.Equal(3, NotificationRetryService.MaxRetries);
    }

    [Fact]
    public async Task RunOnceAsync_dead_letters_after_three_failed_attempts()
    {
        var db = Phase6TestDbContextFactory.Create();
        var sink = new RecordingAdapter(fail: true);
        var registry = new NotificationChannelAdapterRegistry(new INotificationChannelAdapter[]
        {
            sink,
            new InAppSpyAdapter(),
        });
        var tracker = new NotificationDeliveryTracker(db, registry, NullLogger<NotificationDeliveryTracker>.Instance);
        var outbox = new Phase6OperationalEventOutbox(db);
        var retry = new NotificationRetryService(db, tracker, outbox, registry, NullLogger<NotificationRetryService>.Instance);

        // Seed a failed receipt that has already exhausted its retries.
        var receipt = new NotificationDeliveryReceipt
        {
            ReceiptId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            NotificationId = Guid.NewGuid(),
            RecipientId = Guid.NewGuid(),
            Channel = "whatsapp",
            ProviderName = "wa-local",
            Status = "failed",
            RetryCount = 3,
            NextRetryAt = DateTime.UtcNow.AddSeconds(-1),
            CorrelationId = "corr-retry",
            FailureReason = "boom",
            DispatchedAt = DateTime.UtcNow,
        };
        db.NotificationDeliveryReceipts.Add(receipt);
        await db.SaveChangesAsync();

        var processed = await retry.RunOnceAsync();

        Assert.Equal(1, processed);
        var reloaded = await db.NotificationDeliveryReceipts.FirstAsync(r => r.ReceiptId == receipt.ReceiptId);
        Assert.Equal("dead_lettered", reloaded.Status);
        Assert.Null(reloaded.NextRetryAt);
        Assert.Contains(db.Phase6OperationalEvents, e => e.EventKind == "notification_dead_lettered");
    }

    [Fact]
    public async Task RunOnceAsync_retries_and_schedules_next_attempt()
    {
        var db = Phase6TestDbContextFactory.Create();
        var adapter = new RecordingAdapter(fail: true);
        var registry = new NotificationChannelAdapterRegistry(new INotificationChannelAdapter[]
        {
            adapter,
            new InAppSpyAdapter(),
        });
        var tracker = new NotificationDeliveryTracker(db, registry, NullLogger<NotificationDeliveryTracker>.Instance);
        var outbox = new Phase6OperationalEventOutbox(db);
        var retry = new NotificationRetryService(db, tracker, outbox, registry, NullLogger<NotificationRetryService>.Instance);

        var receipt = new NotificationDeliveryReceipt
        {
            ReceiptId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            NotificationId = Guid.NewGuid(),
            RecipientId = Guid.NewGuid(),
            Channel = "whatsapp",
            ProviderName = "wa-local",
            Status = "failed",
            RetryCount = 0,
            NextRetryAt = DateTime.UtcNow.AddSeconds(-1),
            CorrelationId = "corr-retry-2",
            FailureReason = "first",
            DispatchedAt = DateTime.UtcNow,
        };
        db.NotificationDeliveryReceipts.Add(receipt);
        await db.SaveChangesAsync();

        await retry.RunOnceAsync();

        var reloaded = await db.NotificationDeliveryReceipts.FirstAsync(r => r.ReceiptId == receipt.ReceiptId);
        Assert.Equal("failed", reloaded.Status);
        Assert.Equal(1, reloaded.RetryCount);
        Assert.NotNull(reloaded.NextRetryAt);
    }

    private sealed class RecordingAdapter : INotificationChannelAdapter
    {
        private readonly bool _fail;
        public RecordingAdapter(bool fail) => _fail = fail;
        public string Channel => "whatsapp";
        public Task<NotificationDispatchReceipt> DispatchAsync(NotificationDispatchRequest _, CancellationToken __ = default)
            => _fail
                ? throw new InvalidOperationException("simulated provider failure")
                : Task.FromResult(new NotificationDispatchReceipt("ok", Channel));
    }

    private sealed class InAppSpyAdapter : INotificationChannelAdapter
    {
        public string Channel => "in_app";
        public Task<NotificationDispatchReceipt> DispatchAsync(NotificationDispatchRequest _, CancellationToken __ = default)
            => Task.FromResult(new NotificationDispatchReceipt("operator-alert-ok", Channel));
    }
}
