using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Muallimi.Api.Payments.PaymentProviderAdapter;

namespace Muallimi.Api.Payments.Paymob;

/// <summary>
/// Paymob payment provider adapter (v1 Accept API).
///
/// Hosted checkout flow (InitiateCheckoutSessionAsync):
///   1. POST /api/auth/tokens           → auth_token
///   2. POST /api/ecommerce/orders       → order_id
///   3. POST /api/acceptance/payment_keys → payment_token
///   4. Return iframe URL: /api/acceptance/iframes/{IframeId}?payment_token={token}
///
/// Direct charge (ChargeAsync) uses a saved card token for billing cycle renewals.
///
/// Configuration keys (appsettings / env vars):
///   Paymob__ApiKey           — from Paymob dashboard → Settings → Account Info
///   Paymob__IntegrationId    — from Paymob dashboard → Developers → Payment Integrations
///   Paymob__IframeId         — from Paymob dashboard → Developers → iframes
///   Paymob__HmacSecret       — from Paymob dashboard → Settings → Account Info → HMAC Secret
///   Paymob__BaseUrl          — defaults to https://accept.paymob.com (override for sandbox mirrors)
/// </summary>
public sealed class PaymobAdapter : IPaymentProviderAdapter
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _integrationId;
    private readonly string _iframeId;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ProviderName => "paymob";

    public PaymobAdapter(HttpClient http, IConfiguration config)
    {
        _http = http;
        _apiKey       = config["Paymob:ApiKey"]       ?? throw new InvalidOperationException("Paymob:ApiKey is not configured");
        _integrationId = config["Paymob:IntegrationId"] ?? throw new InvalidOperationException("Paymob:IntegrationId is not configured");
        _iframeId     = config["Paymob:IframeId"]     ?? throw new InvalidOperationException("Paymob:IframeId is not configured");
        _baseUrl      = (config["Paymob:BaseUrl"] ?? "https://accept.paymob.com").TrimEnd('/');
    }

    // ── Hosted Checkout ───────────────────────────────────────────────────────

    public async Task<CheckoutSession> InitiateCheckoutSessionAsync(CheckoutSessionRequest request, CancellationToken ct = default)
    {
        var authToken  = await AuthenticateAsync(ct);
        var orderId    = await CreateOrderAsync(authToken, request, ct);
        var paymentKey = await GetPaymentKeyAsync(authToken, orderId, request, ct);
        var iframeUrl  = $"{_baseUrl}/api/acceptance/iframes/{_iframeId}?payment_token={paymentKey}";

        return new CheckoutSession(
            ProviderName: ProviderName,
            CheckoutUrl: iframeUrl,
            ProviderOrderId: orderId.ToString(),
            SessionToken: paymentKey);
    }

    private async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { api_key = _apiKey });
        var res  = await PostJsonAsync($"{_baseUrl}/api/auth/tokens", body, authToken: null, ct);
        var doc  = JsonDocument.Parse(res);
        return doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Paymob auth: missing token in response");
    }

    private async Task<long> CreateOrderAsync(string authToken, CheckoutSessionRequest request, CancellationToken ct)
    {
        var amountCents = (long)Math.Round(request.Amount * 100);
        var body = JsonSerializer.Serialize(new
        {
            auth_token         = authToken,
            delivery_needed    = false,
            amount_cents       = amountCents,
            currency           = request.Currency.ToUpperInvariant(),
            merchant_order_id  = request.SubscriptionId.ToString("N"),
            items              = new[]
            {
                new
                {
                    name         = request.PlanNameEn,
                    amount_cents = amountCents,
                    description  = request.PlanNameAr,
                    quantity     = 1,
                },
            },
        });

        var res = await PostJsonAsync($"{_baseUrl}/api/ecommerce/orders", body, authToken: null, ct);
        var doc = JsonDocument.Parse(res);
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<string> GetPaymentKeyAsync(string authToken, long orderId, CheckoutSessionRequest request, CancellationToken ct)
    {
        var amountCents = (long)Math.Round(request.Amount * 100);
        var body = JsonSerializer.Serialize(new
        {
            auth_token     = authToken,
            amount_cents   = amountCents,
            expiration     = 3600,
            order_id       = orderId,
            billing_data   = new
            {
                apartment        = "NA",
                email            = request.BillingData.Email,
                floor            = "NA",
                first_name       = request.BillingData.FirstName,
                street           = "NA",
                building         = "NA",
                phone_number     = request.BillingData.Phone,
                shipping_method  = "NA",
                postal_code      = "NA",
                city             = request.BillingData.City,
                country          = request.BillingData.Country,
                last_name        = request.BillingData.LastName,
                state            = request.BillingData.State,
            },
            currency         = request.Currency.ToUpperInvariant(),
            integration_id   = int.Parse(_integrationId),
            lock_order_when_paid = true,
            redirect_url     = request.SuccessReturnUrl,
        });

        var res = await PostJsonAsync($"{_baseUrl}/api/acceptance/payment_keys", body, authToken: null, ct);
        var doc = JsonDocument.Parse(res);
        return doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Paymob payment keys: missing token in response");
    }

    // ── Direct Charge (renewals via saved card token) ─────────────────────────

    public async Task<ChargeResult> ChargeAsync(ChargeRequest request, CancellationToken ct = default)
    {
        var authToken   = await AuthenticateAsync(ct);
        var amountCents = (long)Math.Round(request.Amount * 100);

        var body = JsonSerializer.Serialize(new
        {
            source          = new { identifier = request.PaymentMethodRef, subtype = "TOKEN" },
            payment_token   = authToken,
            amount_cents    = amountCents,
            currency        = request.Currency.ToUpperInvariant(),
        });

        try
        {
            var res = await PostJsonAsync($"{_baseUrl}/api/acceptance/payments/pay", body, authToken: null, ct);
            var doc = JsonDocument.Parse(res);
            var success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
            var txId    = doc.RootElement.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null;

            if (success)
                return new ChargeResult("success", txId, null, null);

            var pending = doc.RootElement.TryGetProperty("pending", out var p) && p.GetBoolean();
            var code    = doc.RootElement.TryGetProperty("data", out var data)
                          && data.TryGetProperty("message", out var msg) ? msg.GetString() : null;
            return new ChargeResult(pending ? "pending" : "failed", txId, code, code);
        }
        catch (Exception ex)
        {
            return new ChargeResult("failed", null, "api_error", ex.Message);
        }
    }

    // ── Transaction verification (used by success page fallback) ─────────

    /// <summary>
    /// Fetches a transaction from Paymob by ID and returns a WebhookResult
    /// identical to what ProcessWebhookAsync would return for that transaction.
    /// Used when the webhook didn't fire (e.g. no public URL in dev) but the
    /// success page has the transaction ID from Paymob's redirect URL.
    /// </summary>
    public async Task<WebhookResult> VerifyTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        var authToken = await AuthenticateAsync(ct);
        var res = await _http.GetAsync($"{_baseUrl}/api/acceptance/transactions/{transactionId}?token={authToken}", ct);
        if (!res.IsSuccessStatusCode)
            return new WebhookResult("rejected", null, null);

        var body = await res.Content.ReadAsStringAsync(ct);
        var doc  = JsonDocument.Parse(body);

        var success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
        var pending = doc.RootElement.TryGetProperty("pending", out var p) && p.GetBoolean();
        var txId    = doc.RootElement.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null;

        string? merchantOrderId = null;
        if (doc.RootElement.TryGetProperty("order", out var order)
            && order.TryGetProperty("merchant_order_id", out var mid))
            merchantOrderId = mid.GetString();

        var eventType = success ? "payment_succeeded" : pending ? "payment_pending" : "payment_failed";
        return new WebhookResult("accepted", eventType, txId, merchantOrderId);
    }

    // ── Webhook processing ────────────────────────────────────────────────────

    public Task<WebhookResult> ProcessWebhookAsync(WebhookPayload payload, CancellationToken ct = default)
    {
        try
        {
            var doc  = JsonDocument.Parse(payload.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("obj", out var obj))
                return Task.FromResult(new WebhookResult("rejected", null, null));

            var success  = obj.TryGetProperty("success", out var s) && s.GetBoolean();
            var pending  = obj.TryGetProperty("pending", out var p) && p.GetBoolean();
            var txId     = obj.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null;

            string? merchantOrderId = null;
            if (obj.TryGetProperty("order", out var order)
                && order.TryGetProperty("merchant_order_id", out var mid))
                merchantOrderId = mid.GetString();

            var eventType = success   ? "payment_succeeded"
                          : pending   ? "payment_pending"
                          :             "payment_failed";

            return Task.FromResult(new WebhookResult("accepted", eventType, txId, merchantOrderId));
        }
        catch
        {
            return Task.FromResult(new WebhookResult("rejected", null, null));
        }
    }

    // ── Refund ────────────────────────────────────────────────────────────────

    public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        var authToken   = await AuthenticateAsync(ct);
        var amountCents = (long)Math.Round(request.Amount * 100);

        var body = JsonSerializer.Serialize(new
        {
            auth_token     = authToken,
            transaction_id = request.ProviderReference,
            amount_cents   = amountCents,
        });

        try
        {
            var res = await PostJsonAsync($"{_baseUrl}/api/acceptance/void_refund/refund", body, authToken: null, ct);
            var doc = JsonDocument.Parse(res);
            var success = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
            var txId    = doc.RootElement.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null;
            return success
                ? new RefundResult("success", txId, null)
                : new RefundResult("failed", null, "Refund rejected by Paymob");
        }
        catch (Exception ex)
        {
            return new RefundResult("failed", null, ex.Message);
        }
    }

    // ── Provider-side subscription (not used; billing is managed on our side) ─

    public Task<SubscriptionResult> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct = default)
        => Task.FromResult(new SubscriptionResult("not_applicable", null, "Paymob does not manage subscriptions server-side; billing is handled by MuAllimibilling engine"));

    public Task<SubscriptionResult> CancelSubscriptionAsync(string providerSubscriptionRef, CancellationToken ct = default)
        => Task.FromResult(new SubscriptionResult("not_applicable", null, null));

    // ── Saved payment methods (Paymob card tokens are one-time per checkout) ──

    public Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(Guid tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PaymentMethod>>(Array.Empty<PaymentMethod>());

    public Task<PaymentMethod> AddPaymentMethodAsync(AddPaymentMethodRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("Paymob card tokenisation is handled by the hosted checkout flow; cards are not added separately.");

    public Task RemovePaymentMethodAsync(Guid tenantId, string paymentMethodRef, CancellationToken ct = default)
        => Task.CompletedTask;

    // ── HTTP helper ───────────────────────────────────────────────────────────

    private async Task<string> PostJsonAsync(string url, string jsonBody, string? authToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        if (authToken is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Paymob API error {(int)res.StatusCode} at {url}: {body}");

        return body;
    }
}
