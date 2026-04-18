namespace Muallimi.Api.Payments.PaymentProviderAdapter;

/// <summary>
/// T016 — Swappable payment provider adapter. Local stub implements all
/// scenarios for dev parity; production bindings land at launch time.
/// See contracts/payment-provider-contract.md.
/// </summary>
public interface IPaymentProviderAdapter
{
    string ProviderName { get; }

    Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default);
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default);
    Task<SubscriptionResult> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default);
    Task<SubscriptionResult> CancelSubscriptionAsync(string providerSubscriptionRef, CancellationToken ct = default);
    Task<WebhookResult> ProcessWebhookAsync(WebhookPayload payload, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(Guid tenantId, CancellationToken ct = default);

    // T109 — Saved payment method lifecycle. Add accepts a tokenised representation from the
    // client (provider-specific — e.g. a tokenised card handle) and returns a stable Ref plus
    // masked identifier. Remove detaches by Ref. Adapters must be idempotent on remove.
    Task<PaymentMethod> AddPaymentMethodAsync(AddPaymentMethodRequest request, CancellationToken ct = default);
    Task RemovePaymentMethodAsync(Guid tenantId, string paymentMethodRef, CancellationToken ct = default);
}

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

public record SubscriptionRequest(
    Guid TenantId,
    Guid PlanId,
    string PaymentMethodRef,
    string IdempotencyKey,
    string CorrelationId);

public record SubscriptionResult(string Status, string? ProviderSubscriptionRef, string? FailureReason);

public record WebhookPayload(string Signature, string Body, IDictionary<string, string> Headers);

public record WebhookResult(string Status, string? EventType, string? ProviderReference);

public record PaymentMethod(string Ref, string Type, string MaskedIdentifier);

public record AddPaymentMethodRequest(
    Guid TenantId,
    string Type,
    string ProviderToken,
    string CorrelationId);
