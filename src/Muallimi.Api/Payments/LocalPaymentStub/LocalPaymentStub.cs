using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Muallimi.Api.Payments.PaymentProviderAdapter;

namespace Muallimi.Api.Payments.LocalPaymentStub;

/// <summary>
/// T017 + T113 — Deterministic local payment stub. Scenario selected by amount range
/// per contracts/payment-provider-contract.md:
///   0.01 – 99.99     → success
///   100.00 – 100.99  → insufficient_funds (card declined)
///   101.00 – 101.99  → expired_card
///   102.00 – 102.99  → fraud_hold (pending review)
///   103.00 – 103.99  → network_timeout (transient failure, retryable)
///   104.00 – 104.99  → refund success flow anchor
///   105.00+          → success
/// provider_reference is derived from the idempotency key (SHA-256 → 16 hex chars)
/// so replays return the same reference. Refunds use the suffix ":refund" so the
/// refund reference is derivable but distinct from the charge.
/// </summary>
public class LocalPaymentStub : IPaymentProviderAdapter
{
    private readonly ConcurrentDictionary<Guid, List<PaymentMethod>> _savedMethods = new();

    public string ProviderName => "local_stub";

    public Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default)
    {
        var providerRef = DeterministicRef(request.IdempotencyKey);

        var (status, code, reason) = request.Amount switch
        {
            >= 100m and < 101m => ("failed", "insufficient_funds", "Insufficient funds on payment method"),
            >= 101m and < 102m => ("failed", "expired_card", "Card expired"),
            >= 102m and < 103m => ("pending", "fraud_hold", "Transaction held for fraud review"),
            >= 103m and < 104m => ("failed", "network_timeout", "Transient network timeout — retryable"),
            _ => ("success", (string?)null, (string?)null)
        };

        return Task.FromResult(new ChargeResult(status, providerRef, code, reason));
    }

    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
        => Task.FromResult(new RefundResult("success", DeterministicRef(request.IdempotencyKey + ":refund"), null));

    public Task<SubscriptionResult> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
        => Task.FromResult(new SubscriptionResult("active", DeterministicRef(request.IdempotencyKey), null));

    public Task<SubscriptionResult> CancelSubscriptionAsync(string providerSubscriptionRef, CancellationToken ct = default)
        => Task.FromResult(new SubscriptionResult("cancelled", providerSubscriptionRef, null));

    public Task<WebhookResult> ProcessWebhookAsync(WebhookPayload payload, CancellationToken ct = default)
    {
        var eventType = payload.Headers.TryGetValue("X-Provider-Event-Type", out var evt) && !string.IsNullOrEmpty(evt)
            ? evt
            : "payment_succeeded";
        var providerRef = payload.Headers.TryGetValue("X-Provider-Reference", out var pref) && !string.IsNullOrEmpty(pref)
            ? pref
            : null;
        return Task.FromResult(new WebhookResult("accepted", eventType, providerRef));
    }

    public Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var list = _savedMethods.TryGetValue(tenantId, out var saved)
            ? (IReadOnlyList<PaymentMethod>)saved.ToArray()
            : Array.Empty<PaymentMethod>();
        return Task.FromResult(list);
    }

    public Task<PaymentMethod> AddPaymentMethodAsync(AddPaymentMethodRequest request, CancellationToken ct = default)
    {
        var reference = "pm_" + DeterministicRef(request.TenantId.ToString("N") + ":" + request.ProviderToken);
        var masked = MaskToken(request.ProviderToken);
        var method = new PaymentMethod(reference, request.Type, masked);
        var list = _savedMethods.GetOrAdd(request.TenantId, _ => new List<PaymentMethod>());
        lock (list)
        {
            list.RemoveAll(m => m.Ref == reference);
            list.Add(method);
        }
        return Task.FromResult(method);
    }

    public Task RemovePaymentMethodAsync(Guid tenantId, string paymentMethodRef, CancellationToken ct = default)
    {
        if (_savedMethods.TryGetValue(tenantId, out var list))
        {
            lock (list) { list.RemoveAll(m => m.Ref == paymentMethodRef); }
        }
        return Task.CompletedTask;
    }

    internal static string DeterministicRef(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("local_stub:" + seed));
        return "ls_" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "****";
        if (token.Length <= 4) return new string('*', token.Length);
        return new string('*', Math.Max(0, token.Length - 4)) + token[^4..];
    }
}
