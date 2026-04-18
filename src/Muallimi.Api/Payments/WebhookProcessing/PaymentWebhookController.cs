using System.Text;
using Muallimi.Api.Payments.Idempotency;
using Muallimi.Api.Payments.PaymentProviderAdapter;

namespace Muallimi.Api.Payments.WebhookProcessing;

/// <summary>
/// T041 / T110 / T111 — Incoming payment provider webhook endpoint.
/// 1. Reads the raw body (must stay intact for HMAC verification).
/// 2. Resolves the validator for the provider from WebhookSignatureValidatorRegistry
///    (HMAC-SHA256 for local_stub; provider-specific for production bindings).
/// 3. Applies idempotency dedup on provider_reference + transaction_type with a
///    24-hour window before writing the webhook payload.
/// 4. Delegates parsing to IPaymentProviderAdapter.ProcessWebhookAsync.
/// </summary>
public static class PaymentWebhookEndpoints
{
    public static IEndpointRouteBuilder MapPaymentWebhooks(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/payments/webhooks/{provider}", async (
            string provider,
            HttpContext httpContext,
            IPaymentProviderAdapter adapter,
            IPaymentTransactionService transactions,
            WebhookSignatureValidatorRegistry validators,
            PaymentIdempotencyService idempotency,
            IConfiguration config,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: false);
            var body = await reader.ReadToEndAsync(ct);
            var headers = httpContext.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            var validator = validators.ResolveFor(provider);
            if (validator is null) return Results.NotFound(new { error = $"no signature validator registered for provider '{provider}'" });

            var secret = config[$"Phase6:Payments:WebhookSecret:{provider}"]
                ?? config["Phase6:Payments:WebhookSecret:Default"]
                ?? "muallimi-local-webhook-secret";

            if (!validator.Validate(body, headers, secret))
                return Results.Unauthorized();

            var payload = new WebhookPayload(
                headers.TryGetValue("X-Provider-Signature", out var sig) ? sig : string.Empty, body, headers);
            var result = await adapter.ProcessWebhookAsync(payload, ct);

            if (!string.IsNullOrEmpty(result.ProviderReference) && !string.IsNullOrEmpty(result.EventType))
            {
                var transactionType = ResolveTransactionType(result.EventType);
                var isDuplicate = await idempotency.IsDuplicateWebhookAsync(result.ProviderReference!, transactionType, ct);
                if (isDuplicate)
                {
                    // T111 — Replay inside the 24h window: acknowledge without re-applying state.
                    return Results.Ok(new { status = "duplicate", event_type = result.EventType });
                }

                await transactions.RecordWebhookAsync(result.ProviderReference!, body, result.EventType!, ct);
            }

            return Results.Ok(new { status = result.Status, event_type = result.EventType });
        })
        .WithName("Phase6PaymentWebhook");

        return routes;
    }

    private static string ResolveTransactionType(string eventType) => eventType switch
    {
        "refund_completed" => "refund",
        _ => "charge",
    };
}
