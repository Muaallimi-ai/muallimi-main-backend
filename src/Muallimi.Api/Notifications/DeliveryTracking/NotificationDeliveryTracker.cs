using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Notifications.RetryAndDeadLetter;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Notifications.DeliveryTracking;

/// <summary>
/// T058 (US2) — Creates a <see cref="NotificationDeliveryReceipt"/> for every
/// dispatched Phase 6 notification, wraps the
/// <see cref="INotificationChannelAdapter"/> call, and updates the receipt
/// with the provider status. The retry service
/// (<c>NotificationRetryService</c>) polls <c>status = "queued"</c> /
/// <c>"failed"</c> rows whose <c>next_retry_at</c> is due.
/// </summary>
public sealed record DeliveryDispatchInput(
    Guid TenantId,
    Guid NotificationId,
    Guid RecipientId,
    string Channel,
    string Language,
    string NotificationKind,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string>? Metadata,
    string CorrelationId,
    string ProviderName,
    int RetryCount = 0);

public interface INotificationDeliveryTracker
{
    Task<NotificationDeliveryReceipt> DispatchAsync(DeliveryDispatchInput input, CancellationToken ct = default);
    Task<NotificationDeliveryReceipt?> RedispatchAsync(Guid receiptId, CancellationToken ct = default);
    Task<NotificationDeliveryReceipt?> PollStatusAsync(Guid receiptId, CancellationToken ct = default);
}

public sealed class NotificationDeliveryTracker : INotificationDeliveryTracker
{
    private readonly MuallimiDbContext _db;
    private readonly INotificationChannelAdapterRegistry _channels;
    private readonly ILogger<NotificationDeliveryTracker> _logger;

    public NotificationDeliveryTracker(
        MuallimiDbContext db,
        INotificationChannelAdapterRegistry channels,
        ILogger<NotificationDeliveryTracker> logger)
    {
        _db = db;
        _channels = channels;
        _logger = logger;
    }

    public async Task<NotificationDeliveryReceipt> DispatchAsync(DeliveryDispatchInput input, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var receipt = new NotificationDeliveryReceipt
        {
            ReceiptId = Guid.NewGuid(),
            TenantId = input.TenantId,
            NotificationId = input.NotificationId,
            RecipientId = input.RecipientId,
            Channel = input.Channel,
            ProviderName = input.ProviderName,
            Status = "queued",
            CorrelationId = input.CorrelationId,
            RetryCount = input.RetryCount,
            DispatchedAt = now,
        };
        _db.NotificationDeliveryReceipts.Add(receipt);
        await _db.SaveChangesAsync(ct);

        await DispatchCoreAsync(receipt, input.Language, input.NotificationKind, input.Title, input.Body, input.Metadata, ct);
        await _db.SaveChangesAsync(ct);
        return receipt;
    }

    public async Task<NotificationDeliveryReceipt?> RedispatchAsync(Guid receiptId, CancellationToken ct = default)
    {
        var receipt = await _db.NotificationDeliveryReceipts.FirstOrDefaultAsync(r => r.ReceiptId == receiptId, ct);
        if (receipt is null) return null;
        if (receipt.Status == "delivered" || receipt.Status == "dead_lettered") return receipt;

        receipt.RetryCount += 1;
        receipt.DispatchedAt = DateTime.UtcNow;
        await DispatchCoreAsync(receipt, language: "ar", kind: "retry", title: "retry", body: "retry", metadata: null, ct);
        await _db.SaveChangesAsync(ct);
        return receipt;
    }

    public async Task<NotificationDeliveryReceipt?> PollStatusAsync(Guid receiptId, CancellationToken ct = default)
    {
        // Adapters that support delivery-status polling call back through a
        // webhook; the local stubs deliver synchronously, so we mark the
        // receipt "delivered" after a successful send. Production adapters can
        // extend this to re-query the provider by provider_message_id.
        var receipt = await _db.NotificationDeliveryReceipts.FirstOrDefaultAsync(r => r.ReceiptId == receiptId, ct);
        if (receipt is null) return null;
        if (receipt.Status == "sent")
        {
            receipt.Status = "delivered";
            receipt.DeliveredAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return receipt;
    }

    private async Task DispatchCoreAsync(
        NotificationDeliveryReceipt receipt,
        string language,
        string kind,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        try
        {
            var adapter = _channels.Get(receipt.Channel);
            var request = new NotificationDispatchRequest(
                TenantId: receipt.TenantId,
                RecipientUserId: receipt.RecipientId,
                RecipientEmail: null,
                NotificationKind: kind,
                Language: language,
                Title: title,
                Body: body,
                Metadata: metadata ?? new Dictionary<string, string>(),
                CorrelationId: receipt.CorrelationId);

            var dispatch = await adapter.DispatchAsync(request, ct);
            receipt.ProviderMessageId = dispatch.ReceiptId;
            receipt.Status = "sent";
            receipt.FailureReason = null;
            receipt.NextRetryAt = null;
            _logger.LogInformation(
                "Delivery receipt {ReceiptId} sent via {Channel} provider_message_id={Id}",
                receipt.ReceiptId, receipt.Channel, dispatch.ReceiptId);
        }
        catch (Exception ex)
        {
            receipt.Status = "failed";
            receipt.FailureReason = ex.Message;
            receipt.NextRetryAt = NotificationRetryService.ResolveNextRetryAt(receipt.RetryCount, DateTime.UtcNow);
            _logger.LogWarning(ex,
                "Delivery receipt {ReceiptId} dispatch via {Channel} failed (attempt {Attempt})",
                receipt.ReceiptId, receipt.Channel, receipt.RetryCount);
        }
    }
}

public static class NotificationDeliveryTrackerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase6NotificationDeliveryTracker(this IServiceCollection services)
    {
        services.AddScoped<INotificationDeliveryTracker, NotificationDeliveryTracker>();
        return services;
    }
}
