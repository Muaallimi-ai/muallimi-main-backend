namespace Muallimi.Api.Payments.PaymentProviderAdapter;

/// <summary>
/// T016 — Swappable payment provider adapter.
/// Add a new payment provider: implement this interface + register in DI.
/// No other code changes required.
/// </summary>
public interface IPaymentProviderAdapter
{
    string ProviderName { get; }

    /// <summary>
    /// Initiates a hosted checkout session. The provider returns a URL the browser
    /// should be redirected to. Payment is completed on the provider's hosted page;
    /// the result arrives via webhook (ProcessWebhookAsync).
    /// </summary>
    Task<CheckoutSession> InitiateCheckoutSessionAsync(CheckoutSessionRequest request, CancellationToken ct = default);

    /// <summary>Direct API charge against a saved payment method (used by billing cycle engine for renewals).</summary>
    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default);

    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default);
    Task<SubscriptionResult> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default);
    Task<SubscriptionResult> CancelSubscriptionAsync(string providerSubscriptionRef, CancellationToken ct = default);
    Task<WebhookResult> ProcessWebhookAsync(WebhookPayload payload, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(Guid tenantId, CancellationToken ct = default);
    Task<PaymentMethod> AddPaymentMethodAsync(AddPaymentMethodRequest request, CancellationToken ct = default);
    Task RemovePaymentMethodAsync(Guid tenantId, string paymentMethodRef, CancellationToken ct = default);
}

// ── Checkout ──────────────────────────────────────────────────────────────────

public record CheckoutSessionRequest(
    Guid TenantId,
    Guid SubscriptionId,
    decimal Amount,
    string Currency,
    string PlanNameAr,
    string PlanNameEn,
    BillingData BillingData,
    string SuccessReturnUrl,
    string FailureReturnUrl,
    string WebhookCallbackUrl,
    string CorrelationId);

public record BillingData(
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string Country = "EG",
    string City = "Cairo",
    string State = "Cairo");

public record CheckoutSession(
    string ProviderName,
    string CheckoutUrl,
    string? ProviderOrderId,
    string? SessionToken);

// ── Charge / Refund ───────────────────────────────────────────────────────────

public record ChargeRequest(
    Guid TenantId,
    Guid InvoiceId,
    decimal Amount,
    string Currency,
    string PaymentMethodRef,
    string IdempotencyKey,
    string CorrelationId);

public record ChargeResult(
    string Status,
    string? ProviderReference,
    string? FailureCode,
    string? FailureReason);

public record RefundRequest(
    string ProviderReference,
    decimal Amount,
    string Currency,
    string IdempotencyKey,
    string CorrelationId);

public record RefundResult(string Status, string? ProviderReference, string? FailureReason);

// ── Subscription (provider-side recurring) ────────────────────────────────────

public record SubscriptionRequest(
    Guid TenantId,
    Guid PlanId,
    string PaymentMethodRef,
    string IdempotencyKey,
    string CorrelationId);

public record SubscriptionResult(string Status, string? ProviderSubscriptionRef, string? FailureReason);

// ── Webhook ───────────────────────────────────────────────────────────────────

public record WebhookPayload(string Signature, string Body, IDictionary<string, string> Headers);

public record WebhookResult(
    string Status,
    string? EventType,
    string? ProviderReference,
    /// <summary>Our internal subscription/order ID that was passed to the provider when creating the order.</summary>
    string? MerchantOrderId = null);

// ── Payment methods ───────────────────────────────────────────────────────────

public record PaymentMethod(string Ref, string Type, string MaskedIdentifier);

public record AddPaymentMethodRequest(
    Guid TenantId,
    string Type,
    string ProviderToken,
    string CorrelationId);
