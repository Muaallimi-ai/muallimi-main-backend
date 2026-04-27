using System.Text;
using Muallimi.Api.Billing.SubscriptionLifecycle;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Payments.Idempotency;
using Muallimi.Api.Payments.PaymentProviderAdapter;
using Muallimi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Muallimi.Api.Payments.WebhookProcessing;

/// <summary>
/// POST /api/v1/payments/webhooks/{provider}
///
/// Handles two flows:
///
/// 1. NEW REGISTRATION — merchant_order_id = PendingRegistration.Id
///    Payment confirmed → PaymentRegistrationService.CompleteFromPaymentAsync
///    → User + Tenant + ParentProfile + Subscription created atomically.
///    → PaymentSessionToken stored so the success page can exchange it for a JWT.
///
/// 2. SUBSCRIPTION RENEWAL — merchant_order_id = Subscription.SubscriptionId
///    Payment confirmed → SubscriptionLifecycleService.ActivateFromPaymentAsync.
/// </summary>
public static class PaymentWebhookEndpoints
{
    public static IEndpointRouteBuilder MapPaymentWebhooks(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/payments/webhooks/{provider}", async (
            string provider,
            HttpContext httpContext,
            IPaymentProviderAdapterRegistry adapterRegistry,
            IPaymentRegistrationService paymentRegistration,
            ISubscriptionLifecycleService lifecycle,
            MuallimiDbContext db,
            IPaymentTransactionService transactions,
            WebhookSignatureValidatorRegistry validators,
            PaymentIdempotencyService idempotency,
            IConfiguration config,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: false);
            var body = await reader.ReadToEndAsync(ct);
            var headers = httpContext.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            // Paymob (new API) sends the HMAC as a query-string parameter.
            // Merge it into the headers dict so validators can find it in one place.
            foreach (var qp in httpContext.Request.Query)
                headers[$"query:{qp.Key}"] = qp.Value.ToString();

            var adapter = adapterRegistry.Resolve(provider);
            if (adapter is null)
                return Results.NotFound(new { error = $"No payment adapter registered for provider '{provider}'" });

            var validator = validators.ResolveFor(provider);
            if (validator is null)
                return Results.NotFound(new { error = $"No signature validator registered for provider '{provider}'" });

            var secret = config["Paymob:HmacSecret"]
                ?? config[$"Phase6:Payments:WebhookSecret:{provider}"]
                ?? config["Phase6:Payments:WebhookSecret:Default"]
                ?? string.Empty;

            if (!validator.Validate(body, headers, secret))
                return Results.Unauthorized();

            var payload = new WebhookPayload(
                headers.TryGetValue("X-Provider-Signature", out var sig) ? sig : string.Empty,
                body,
                headers);

            var result = await adapter.ProcessWebhookAsync(payload, ct);

            // Idempotency dedup.
            if (!string.IsNullOrEmpty(result.ProviderReference) && !string.IsNullOrEmpty(result.EventType))
            {
                var transactionType = result.EventType == "refund_completed" ? "refund" : "charge";
                var isDuplicate = await idempotency.IsDuplicateWebhookAsync(result.ProviderReference!, transactionType, ct);
                if (isDuplicate)
                    return Results.Ok(new { status = "duplicate", event_type = result.EventType });

                await transactions.RecordWebhookAsync(result.ProviderReference!, body, result.EventType!, ct);
            }

            if (result.EventType == "payment_succeeded"
                && !string.IsNullOrEmpty(result.MerchantOrderId)
                && Guid.TryParse(result.MerchantOrderId, out var merchantId))
            {
                var correlationId = headers.TryGetValue("X-Correlation-Id", out var cid) && !string.IsNullOrEmpty(cid)
                    ? cid
                    : Guid.NewGuid().ToString();

                // Determine whether this is a new registration or a renewal.
                var isPendingRegistration = await db.PendingRegistrations
                    .AnyAsync(p => p.Id == merchantId, ct);

                if (isPendingRegistration)
                {
                    // New registration — create account atomically.
                    await paymentRegistration.CompleteFromPaymentAsync(
                        merchantId,
                        result.ProviderReference ?? string.Empty,
                        correlationId,
                        ct);
                }
                else
                {
                    // Subscription renewal — activate existing subscription.
                    await lifecycle.ActivateFromPaymentAsync(
                        merchantId,
                        result.ProviderReference ?? string.Empty,
                        correlationId,
                        ct);
                }
            }

            return Results.Ok(new { status = result.Status, event_type = result.EventType });
        })
        .WithName("Phase6PaymentWebhook");

        return routes;
    }
}
