using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Notifications.DeliveryTracking;
using Muallimi.Api.Notifications.ProductionProviderBindings;
using Muallimi.Api.Parents.ParentNotifications;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Notifications;

public class NotificationDeliveryTrackerTests
{
    private static NotificationDeliveryTracker Build(INotificationChannelAdapter adapter, out Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        db = Phase6TestDbContextFactory.Create();
        var registry = new NotificationChannelAdapterRegistry(new[] { adapter });
        return new NotificationDeliveryTracker(db, registry, NullLogger<NotificationDeliveryTracker>.Instance);
    }

    [Fact]
    public async Task DispatchAsync_writes_receipt_and_marks_sent_on_success()
    {
        var sink = new LocalWhatsAppProviderSink();
        var adapter = new WhatsAppProviderBinding(sink, NullLogger<WhatsAppProviderBinding>.Instance);
        var tracker = Build(adapter, out var db);

        var input = new DeliveryDispatchInput(
            TenantId: Guid.NewGuid(),
            NotificationId: Guid.NewGuid(),
            RecipientId: Guid.NewGuid(),
            Channel: "whatsapp",
            Language: "ar",
            NotificationKind: "billing.payment_failed",
            Title: "فشل الدفع",
            Body: "تعذّر إتمام الدفع. يرجى تحديث بيانات البطاقة.",
            Metadata: null,
            CorrelationId: "corr-delivery-1",
            ProviderName: "wa-local");

        var receipt = await tracker.DispatchAsync(input);

        Assert.Equal("sent", receipt.Status);
        Assert.StartsWith("wa-local-", receipt.ProviderMessageId);
        Assert.Null(receipt.NextRetryAt);
        Assert.Single(db.NotificationDeliveryReceipts);
        var captured = Assert.Single(sink.DrainForInspection());
        Assert.Contains("تعذّر", captured.Body); // Arabic diacritic-bearing text preserved verbatim
    }

    [Fact]
    public async Task DispatchAsync_schedules_retry_on_failure()
    {
        var adapter = new ExplodingAdapter();
        var tracker = Build(adapter, out var db);

        var receipt = await tracker.DispatchAsync(new DeliveryDispatchInput(
            TenantId: Guid.NewGuid(),
            NotificationId: Guid.NewGuid(),
            RecipientId: Guid.NewGuid(),
            Channel: "whatsapp",
            Language: "ar",
            NotificationKind: "engagement.weekly_report_ready",
            Title: "T",
            Body: "B",
            Metadata: null,
            CorrelationId: "corr-fail-1",
            ProviderName: "wa-local"));

        Assert.Equal("failed", receipt.Status);
        Assert.NotNull(receipt.NextRetryAt);
        Assert.NotNull(receipt.FailureReason);
    }

    private sealed class ExplodingAdapter : INotificationChannelAdapter
    {
        public string Channel => "whatsapp";
        public Task<NotificationDispatchReceipt> DispatchAsync(NotificationDispatchRequest _, CancellationToken __ = default)
            => throw new InvalidOperationException("provider down");
    }
}
