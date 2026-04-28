using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Identity.Filters;
using Muallimi.Api.Identity.Services;

namespace Muallimi.Api.Identity.Startup;

public record PaymentVerifyRequest(Guid PendingId, string Nonce, string TransactionId);

/// <summary>
/// T043 — Root route group for every Phase 9 Identity endpoint. User
/// stories (US1 public auth, US2 parent-children, US3 admin) will
/// register their own <c>MapGroup("...")</c> children inside this group
/// so they inherit CORS + authorization wiring in one place.
///
/// This Part 3 scaffold only exposes a version probe so the frontend
/// and the smoke script can confirm the module is wired without
/// running the full auth flow.
/// </summary>
public static class IdentityEndpointRouteBuilderExtensions
{
    public const string IdentityRoutePrefix = "/api/auth";

    public static RouteGroupBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(IdentityRoutePrefix)
            .WithTags("Identity")
            .RequireCors(IdentityServiceCollectionExtensions.IdentityCorsPolicy)
            .AddEndpointFilter<IdentityAuthorizationFilter>();

        // Health probe. Does NOT require auth — intentional. Smoke
        // script uses this to verify the module is mapped before the
        // first /register + /login.
        group.MapGet("/_module", () => Results.Ok(new
        {
            module = "identity",
            phase = 9,
            status = "us1",
        }));

        // US1: public auth endpoints + authenticated session endpoints.
        group.MapPublicAuthEndpoints();
        group.MapAuthenticatedEndpoints();

        // US2: parent-children management.
        group.MapParentChildrenEndpoints();

        // Add-child redesign Phase 4: parent profile-switch endpoints.
        group.MapParentSwitchEndpoints();

        // US3: admin user management + forced-rotation gate + invitation accept.
        group.MapAdminUserEndpoints();

        // Payment session polling — called by the success page to exchange
        // (pending_id + nonce) for a JWT after the Paymob webhook fires.
        group.MapGet("/payment-session", async (
            Guid pending_id,
            string nonce,
            IPaymentRegistrationService paymentRegistration,
            CancellationToken ct) =>
        {
            if (pending_id == Guid.Empty || string.IsNullOrWhiteSpace(nonce))
                return Results.BadRequest(new { error = "pending_id and nonce are required" });

            var result = await paymentRegistration.ExchangeSessionTokenAsync(pending_id, nonce, ct);

            if (result is null)
                return Results.NotFound(new { status = "pending", message = "Payment not confirmed yet — retry in 2 seconds" });

            return Results.Ok(new
            {
                status = "ready",
                access_token  = result.Value.AccessToken,
                refresh_token = result.Value.RefreshToken,
            });
        });


        // ── Dev-only fallback: verify a Paymob transaction directly ──────────
        // Only available in Development environment. When ngrok is not running
        // (e.g. a colleague's laptop), the success page calls this after the
        // webhook polling times out. It verifies the transaction with Paymob's
        // API and completes registration without needing a webhook at all.
        // In Production this endpoint returns 404 — the real webhook must fire.
        group.MapPost("/payment-verify", async (
            PaymentVerifyRequest body,
            IPaymentRegistrationService paymentRegistration,
            Muallimi.Api.Payments.Paymob.PaymobAdapter paymobAdapter,
            IWebHostEnvironment env,
            CancellationToken ct) =>
        {
            if (!env.IsDevelopment())
                return Results.NotFound();

            if (body.PendingId == Guid.Empty || string.IsNullOrWhiteSpace(body.Nonce)
                || string.IsNullOrWhiteSpace(body.TransactionId))
                return Results.BadRequest(new { error = "pending_id, nonce and transaction_id are required" });

            // If webhook already processed it, just exchange the token.
            var existing = await paymentRegistration.ExchangeSessionTokenAsync(body.PendingId, body.Nonce, ct);
            if (existing is not null)
                return Results.Ok(new { status = "ready", access_token = existing.Value.AccessToken, refresh_token = existing.Value.RefreshToken });

            // Call Paymob directly to verify the transaction.
            var verification = await paymobAdapter.VerifyTransactionAsync(body.TransactionId, ct);
            if (verification.EventType != "payment_succeeded")
                return Results.BadRequest(new { error = "Transaction not confirmed as successful by Paymob" });

            if (string.IsNullOrEmpty(verification.MerchantOrderId)
                || !Guid.TryParse(verification.MerchantOrderId.Replace("-", ""), out var parsedMerchantId)
                || parsedMerchantId != body.PendingId)
            {
                // Try without stripping hyphens
                if (!Guid.TryParse(verification.MerchantOrderId, out parsedMerchantId)
                    || parsedMerchantId != body.PendingId)
                    return Results.BadRequest(new { error = "Transaction does not match this registration" });
            }

            await paymentRegistration.CompleteFromPaymentAsync(
                body.PendingId,
                verification.ProviderReference ?? body.TransactionId,
                Guid.NewGuid().ToString(),
                ct);

            var tokens = await paymentRegistration.ExchangeSessionTokenAsync(body.PendingId, body.Nonce, ct);
            if (tokens is null)
                return Results.Problem("Account created but session token not found — retry in 1 second");

            return Results.Ok(new { status = "ready", access_token = tokens.Value.AccessToken, refresh_token = tokens.Value.RefreshToken });
        });

        return group;
    }
}
