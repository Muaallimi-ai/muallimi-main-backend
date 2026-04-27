using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Payments.PaymentProviderAdapter;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Payments;

/// <summary>
/// POST /api/v1/payments/initiate
///
/// Called immediately after the registration form collects parent info + plan selection.
/// No authentication required — the identity is verified via pendingId + nonce
/// which were returned by POST /api/auth/register/parent.
///
/// Steps:
///   1. Validate the pending registration (exists, not expired, nonce matches).
///   2. Update the pending registration with the chosen plan.
///   3. Call PaymentProviderAdapterRegistry.GetDefault().InitiateCheckoutSessionAsync.
///   4. Return the provider checkout URL + pending_id for the success page.
///
/// On Paymob webhook success, CompleteFromPaymentAsync creates the real account.
/// </summary>
public static class PaymentInitiateEndpoints
{
    public static IEndpointRouteBuilder MapPaymentInitiateEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/payments/initiate", async (
            PaymentInitiateRequest body,
            IPaymentProviderAdapterRegistry registry,
            IPaymentRegistrationService paymentRegistration,
            MuallimiDbContext db,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (body.PendingId == Guid.Empty || string.IsNullOrWhiteSpace(body.Nonce))
                return Results.BadRequest(new { error = "pending_id and nonce are required" });

            var pending = await paymentRegistration.PrepareForPaymentAsync(
                body.PendingId, body.Nonce, body.PlanId, ct);

            if (pending is null)
                return Results.BadRequest(new { error = "Registration session not found or expired. Please register again." });

            var plan = await db.SubscriptionPlans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlanId == body.PlanId && p.IsActive, ct);

            if (plan is null)
                return Results.BadRequest(new { error = "Plan not found or inactive" });

            var correlationId = Guid.NewGuid().ToString();
            var frontendBase = (config["App:FrontendBaseUrl"] ?? "http://localhost:3000").TrimEnd('/');
            var backendBase  = await new NgrokPublicUrlResolver(
                new HttpClient(), config).GetWebhookBaseUrlAsync(ct);

            var adapter = registry.GetDefault();

            // merchant_order_id = pending registration ID so the webhook can find it.
            var session = await adapter.InitiateCheckoutSessionAsync(new CheckoutSessionRequest(
                TenantId: Guid.Empty,          // tenant doesn't exist yet
                SubscriptionId: body.PendingId, // re-used as merchant_order_id
                Amount: plan.PriceEgp,
                Currency: "EGP",
                PlanNameAr: plan.PlanNameAr,
                PlanNameEn: plan.PlanNameEn,
                BillingData: new BillingData(
                    FirstName: SplitFirstName(pending.FullName),
                    LastName:  SplitLastName(pending.FullName),
                    Email:     pending.Email,
                    Phone:     string.IsNullOrEmpty(pending.PhoneNumber) ? "+20000000000" : pending.PhoneNumber),
                SuccessReturnUrl: $"{frontendBase}/payment/success?pending_id={body.PendingId}&nonce={Uri.EscapeDataString(body.Nonce)}",
                FailureReturnUrl: $"{frontendBase}/payment/failure?pending_id={body.PendingId}",
                WebhookCallbackUrl: $"{backendBase}/api/v1/payments/webhooks/{adapter.ProviderName}",
                CorrelationId: correlationId), ct);

            return Results.Ok(new
            {
                checkout_url = session.CheckoutUrl,
                provider_name = session.ProviderName,
                pending_id = body.PendingId,
            });
        })
        .WithName("PaymentInitiate");  // No RequireAuthorization — verified via pending_id + nonce

        return routes;
    }

    private static string SplitFirstName(string fullName)
    {
        var parts = fullName.Trim().Split(' ', 2);
        return parts[0];
    }

    private static string SplitLastName(string fullName)
    {
        var parts = fullName.Trim().Split(' ', 2);
        return parts.Length > 1 ? parts[1] : parts[0];
    }
}

public record PaymentInitiateRequest(Guid PendingId, string Nonce, Guid PlanId);
