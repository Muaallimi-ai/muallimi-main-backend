using Microsoft.Extensions.Logging;
using Muallimi.Api.Notifications;
using Muallimi.Api.Notifications.DeliveryTracking;

namespace Muallimi.Api.Billing;

/// <summary>
/// T061 (US2) — Wires billing lifecycle signals (payment success, payment
/// failure, grace-period start, subscription expiry) to the Phase 6
/// <see cref="INotificationDeliveryTracker"/>.
///
/// Each event resolves an Arabic+English template, picks the <c>email</c>
/// channel by default (billing statements are inherently email-shaped), and
/// lets <see cref="QuietHoursPolicy"/> bypass quiet hours for critical
/// categories (payment failure, grace start, expiry) per the provider
/// contract. Non-critical events (payment success) honour quiet hours.
/// </summary>
public interface IBillingNotificationDispatcher
{
    Task DispatchPaymentSucceededAsync(BillingNotificationContext ctx, CancellationToken ct = default);
    Task DispatchPaymentFailedAsync(BillingNotificationContext ctx, string? failureCode, CancellationToken ct = default);
    Task DispatchGracePeriodStartedAsync(BillingNotificationContext ctx, DateTime graceEnd, CancellationToken ct = default);
    Task DispatchSubscriptionExpiredAsync(BillingNotificationContext ctx, CancellationToken ct = default);
}

public sealed record BillingNotificationContext(
    Guid TenantId,
    Guid RecipientId,
    Guid NotificationId,
    string Language,
    string CorrelationId);

public sealed class BillingNotificationDispatcher : IBillingNotificationDispatcher
{
    private const string DefaultChannel = "email";
    private const string ProviderName = "phase6-email-local";

    private readonly INotificationDeliveryTracker _tracker;
    private readonly ILogger<BillingNotificationDispatcher> _logger;

    public BillingNotificationDispatcher(
        INotificationDeliveryTracker tracker,
        ILogger<BillingNotificationDispatcher> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public Task DispatchPaymentSucceededAsync(BillingNotificationContext ctx, CancellationToken ct = default)
        => DispatchAsync(ctx,
            kind: "billing.payment_succeeded",
            titleAr: "تم استلام الدفع",
            bodyAr: "شكرًا لك. تم استلام دفعتك بنجاح.",
            titleEn: "Payment received",
            bodyEn: "Thank you. Your payment was received successfully.",
            ct);

    public Task DispatchPaymentFailedAsync(BillingNotificationContext ctx, string? failureCode, CancellationToken ct = default)
        => DispatchAsync(ctx,
            kind: "billing.payment_failed",
            titleAr: "تعذّر إتمام الدفع",
            bodyAr: $"لم نتمكن من إتمام الدفع. سبب الرفض: {failureCode ?? "غير محدد"}.",
            titleEn: "Payment could not be completed",
            bodyEn: $"We were unable to process your payment. Failure code: {failureCode ?? "unspecified"}.",
            ct);

    public Task DispatchGracePeriodStartedAsync(BillingNotificationContext ctx, DateTime graceEnd, CancellationToken ct = default)
        => DispatchAsync(ctx,
            kind: "billing.grace_period_started",
            titleAr: "تم تفعيل فترة السماح",
            bodyAr: $"تم تفعيل فترة سماح تنتهي في {graceEnd:yyyy-MM-dd}. يرجى تحديث وسيلة الدفع.",
            titleEn: "Grace period started",
            bodyEn: $"A grace period is active until {graceEnd:yyyy-MM-dd}. Please update your payment method.",
            ct);

    public Task DispatchSubscriptionExpiredAsync(BillingNotificationContext ctx, CancellationToken ct = default)
        => DispatchAsync(ctx,
            kind: "billing.subscription_expired",
            titleAr: "انتهى الاشتراك",
            bodyAr: "انتهت صلاحية اشتراكك. يمكنك تجديد الاشتراك في أي وقت.",
            titleEn: "Subscription expired",
            bodyEn: "Your subscription has expired. You can re-subscribe at any time.",
            ct);

    private async Task DispatchAsync(
        BillingNotificationContext ctx,
        string kind,
        string titleAr,
        string bodyAr,
        string titleEn,
        string bodyEn,
        CancellationToken ct)
    {
        var isArabic = string.Equals(ctx.Language, "ar", StringComparison.OrdinalIgnoreCase);
        try
        {
            await _tracker.DispatchAsync(new DeliveryDispatchInput(
                TenantId: ctx.TenantId,
                NotificationId: ctx.NotificationId,
                RecipientId: ctx.RecipientId,
                Channel: DefaultChannel,
                Language: isArabic ? "ar" : "en",
                NotificationKind: kind,
                Title: isArabic ? titleAr : titleEn,
                Body: isArabic ? bodyAr : bodyEn,
                Metadata: new Dictionary<string, string>
                {
                    ["billing_notification_kind"] = kind,
                    ["quiet_hours_override"] = QuietHoursPolicy.IsCritical(kind) ? "true" : "false",
                },
                CorrelationId: ctx.CorrelationId,
                ProviderName: ProviderName), ct);
        }
        catch (Exception ex)
        {
            // Billing operations must not fail because a notification dispatch
            // errored. The delivery-receipt row (if written) carries the
            // failure; the retry service picks it up. Swallow + log only.
            _logger.LogWarning(ex,
                "Billing notification {Kind} dispatch failed for tenant {TenantId} correlation {CorrelationId}",
                kind, ctx.TenantId, ctx.CorrelationId);
        }
    }
}

public static class BillingNotificationServiceCollectionExtensions
{
    public static IServiceCollection AddPhase6BillingNotificationDispatcher(this IServiceCollection services)
    {
        services.AddScoped<IBillingNotificationDispatcher, BillingNotificationDispatcher>();
        return services;
    }
}
