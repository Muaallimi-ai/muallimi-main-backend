using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Billing;
using Muallimi.Api.Notifications.DeliveryTracking;
using Muallimi.Api.Notifications.ProductionProviderBindings;
using Muallimi.Api.Parents.ParentNotifications;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Notifications;

public class BillingNotificationDispatcherTests
{
    private static (BillingNotificationDispatcher dispatcher, LocalWhatsAppProviderSink sink, Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
        Build()
    {
        var db = Phase6TestDbContextFactory.Create();
        var sink = new LocalWhatsAppProviderSink();
        // Billing dispatcher defaults to the "email" channel. For this test we
        // wire the WhatsApp sink under the "email" key so we can inspect what
        // would have been sent without depending on the Phase 4 HTTP stubs.
        var emailAdapter = new PassthroughAdapter(sink, "email");
        var registry = new NotificationChannelAdapterRegistry(new INotificationChannelAdapter[] { emailAdapter });
        var tracker = new NotificationDeliveryTracker(db, registry, NullLogger<NotificationDeliveryTracker>.Instance);
        return (new BillingNotificationDispatcher(tracker, NullLogger<BillingNotificationDispatcher>.Instance), sink, db);
    }

    [Fact]
    public async Task DispatchPaymentFailedAsync_writes_arabic_body_with_failure_code()
    {
        var (d, sink, db) = Build();
        var ctx = new BillingNotificationContext(
            TenantId: Guid.NewGuid(),
            RecipientId: Guid.NewGuid(),
            NotificationId: Guid.NewGuid(),
            Language: "ar",
            CorrelationId: "corr-bill-failed");

        await d.DispatchPaymentFailedAsync(ctx, "insufficient_funds");

        var receipt = Assert.Single(db.NotificationDeliveryReceipts);
        Assert.Equal("sent", receipt.Status);
        var captured = Assert.Single(sink.DrainForInspection());
        Assert.Contains("لم نتمكن", captured.Body);
        Assert.Contains("insufficient_funds", captured.Body);
    }

    [Fact]
    public async Task DispatchPaymentSucceededAsync_honours_language_english()
    {
        var (d, sink, db) = Build();
        var ctx = new BillingNotificationContext(
            TenantId: Guid.NewGuid(),
            RecipientId: Guid.NewGuid(),
            NotificationId: Guid.NewGuid(),
            Language: "en",
            CorrelationId: "corr-bill-ok");

        await d.DispatchPaymentSucceededAsync(ctx);

        var receipt = Assert.Single(db.NotificationDeliveryReceipts);
        Assert.Equal("sent", receipt.Status);
        var captured = Assert.Single(sink.DrainForInspection());
        Assert.Equal("en", captured.Language);
        Assert.Contains("received", captured.Body);
    }

    private sealed class PassthroughAdapter : INotificationChannelAdapter
    {
        private readonly IWhatsAppProviderSink _sink;
        public PassthroughAdapter(IWhatsAppProviderSink sink, string channel)
        {
            _sink = sink;
            Channel = channel;
        }
        public string Channel { get; }
        public async Task<NotificationDispatchReceipt> DispatchAsync(NotificationDispatchRequest r, CancellationToken ct = default)
        {
            var id = await _sink.SendAsync(new WhatsAppMessage(
                r.TenantId, r.ParentProfileId, r.NotificationKind, r.Language,
                r.Title, r.Body, r.CorrelationId, r.Metadata), ct);
            return new NotificationDispatchReceipt(id, Channel);
        }
    }
}
