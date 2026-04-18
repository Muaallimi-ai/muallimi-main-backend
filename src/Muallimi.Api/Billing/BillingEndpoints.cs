using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Billing.InvoiceGeneration;
using Muallimi.Api.Billing.SubscriptionLifecycle;
using Muallimi.Api.Billing.SubscriptionPlans;
using Muallimi.Api.Payments.PaymentProviderAdapter;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Billing;

/// <summary>
/// T044 — Billing API endpoints per billing-subscription-contract.md. All
/// endpoints honour X-Tenant-Id and X-Correlation-Id; response bodies contain
/// locale-resolved plan names and currency-formatted amounts.
/// </summary>
public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/billing/plans", async (
            string? plan_type,
            string? locale,
            ISubscriptionPlanService plans,
            CancellationToken ct) =>
        {
            var list = await plans.ListAsync(plan_type, includeInactive: false, ct);
            var loc = locale == "en" ? "en" : "ar";
            var result = list.Select(p => new
            {
                plan_id = p.PlanId,
                plan_name = loc == "ar" ? p.PlanNameAr : p.PlanNameEn,
                tier = p.Tier,
                plan_type = p.PlanType,
                price = FormatCurrency(p.PriceEgp, "egp", loc),
                currency = "egp",
                billing_cycle = p.BillingCycle,
                seat_limit = p.SeatLimit,
                feature_entitlements = ParseJson(p.FeatureEntitlements),
                usage_limits = ParseJson(p.UsageLimits),
            });
            return Results.Ok(new { plans = result });
        });

        routes.MapPost("/api/v1/billing/plans", async (
            HttpContext http,
            ISubscriptionPlanService plans,
            SubscriptionPlanInput input,
            CancellationToken ct) =>
        {
            var operatorId = ResolveOperatorId(http);
            var plan = await plans.CreateAsync(input, operatorId, ct);
            return Results.Created($"/api/v1/billing/plans/{plan.PlanId}", plan);
        });

        routes.MapPost("/api/v1/billing/subscriptions", async (
            HttpContext http,
            ISubscriptionLifecycleService lifecycle,
            IPhase5LicenseSyncService licenseSync,
            MuallimiDbContext db,
            CreateSubscriptionBody body,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();

            var subscription = await lifecycle.CreateAsync(
                new SubscriptionCreateInput(tenantId, body.PlanId, body.PaymentMethodRef, correlationId), ct);

            if (subscription.PlanType == "school")
            {
                var schoolTenantId = ResolveSchoolTenantId(http) ?? tenantId;
                await licenseSync.SyncFromSubscriptionAsync(schoolTenantId, subscription.SubscriptionId, ct);
            }

            var plan = await db.SubscriptionPlans.AsNoTracking().FirstAsync(p => p.PlanId == subscription.PlanId, ct);
            return Results.Ok(new
            {
                subscription_id = subscription.SubscriptionId,
                status = subscription.Status,
                current_period_start = subscription.CurrentPeriodStart,
                current_period_end = subscription.CurrentPeriodEnd,
                plan = new { plan_id = plan.PlanId, plan_name = plan.PlanNameAr, tier = plan.Tier },
            });
        });

        routes.MapGet("/api/v1/billing/subscriptions/current", async (
            HttpContext http,
            ISubscriptionLifecycleService lifecycle,
            MuallimiDbContext db,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var sub = await lifecycle.GetCurrentAsync(tenantId, ct);
            if (sub is null) return Results.NotFound();
            var plan = await db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == sub.PlanId, ct);
            var loc = http.Request.Query["locale"].FirstOrDefault() == "en" ? "en" : "ar";
            return Results.Ok(new
            {
                subscription_id = sub.SubscriptionId,
                plan = plan is null ? null : (object)new
                {
                    plan_id = plan.PlanId,
                    plan_name = loc == "ar" ? plan.PlanNameAr : plan.PlanNameEn,
                    tier = plan.Tier,
                    price = FormatCurrency(plan.PriceEgp, "egp", loc),
                    currency = "egp",
                    billing_cycle = plan.BillingCycle,
                },
                status = sub.Status,
                current_period_start = sub.CurrentPeriodStart,
                current_period_end = sub.CurrentPeriodEnd,
                trial_end = sub.TrialEnd,
                grace_period_end = sub.GracePeriodEnd,
                cancelled_at = sub.CancelledAt,
            });
        });

        routes.MapPut("/api/v1/billing/subscriptions/current/plan", async (
            HttpContext http,
            ISubscriptionLifecycleService lifecycle,
            IPhase5LicenseSyncService licenseSync,
            ChangePlanBody body,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
            var result = await lifecycle.ChangePlanAsync(tenantId, body.NewPlanId, correlationId, ct);
            if (result is null) return Results.NotFound();

            var current = await lifecycle.GetCurrentAsync(tenantId, ct);
            if (current is { PlanType: "school" })
            {
                var schoolTenantId = ResolveSchoolTenantId(http) ?? tenantId;
                await licenseSync.SyncFromSubscriptionAsync(schoolTenantId, current.SubscriptionId, ct);
            }

            return Results.Ok(result);
        });

        routes.MapPost("/api/v1/billing/subscriptions/current/cancel", async (
            HttpContext http,
            ISubscriptionLifecycleService lifecycle,
            CancelBody body,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
            var result = await lifecycle.CancelAsync(tenantId, body.Reason, correlationId, ct);
            if (result is null) return Results.NotFound();
            return Results.Ok(new { subscription_id = result.SubscriptionId, status = "cancelled", effective_at = result.EffectiveAt });
        });

        routes.MapGet("/api/v1/billing/invoices", async (
            HttpContext http,
            IInvoiceGenerationService invoices,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var limit = int.TryParse(http.Request.Query["limit"], out var l) ? l : 20;
            DateTime? before = DateTime.TryParse(http.Request.Query["cursor"], out var c) ? c : null;
            var loc = http.Request.Query["locale"].FirstOrDefault() == "en" ? "en" : "ar";
            var list = await invoices.ListForTenantAsync(tenantId, limit, before, ct);
            var mapped = list.Select(i => new
            {
                invoice_id = i.InvoiceId,
                invoice_number = i.InvoiceNumber,
                period_start = i.PeriodStart,
                period_end = i.PeriodEnd,
                total = FormatCurrency(i.Total, i.Currency, loc),
                currency = i.Currency,
                payment_status = i.PaymentStatus,
                issued_at = i.IssuedAt,
                pdf_download_url = $"/api/v1/billing/invoices/{i.InvoiceId}/pdf?locale={loc}",
            });
            var next = list.Count > 0 ? list.Min(i => i.IssuedAt).ToString("O", CultureInfo.InvariantCulture) : null;
            return Results.Ok(new { invoices = mapped, next_cursor = list.Count == limit ? next : null });
        });

        routes.MapGet("/api/v1/billing/invoices/{invoiceId:guid}/pdf", async (
            Guid invoiceId,
            HttpContext http,
            IInvoiceGenerationService invoices,
            CancellationToken ct) =>
        {
            var loc = http.Request.Query["locale"].FirstOrDefault() == "en" ? "en" : "ar";
            var result = await invoices.RenderPdfAsync(invoiceId, loc, ct);
            if (result is null) return Results.NotFound();
            return Results.File(result.Value.Pdf, "application/pdf", result.Value.FileName);
        });

        routes.MapGet("/api/v1/billing/entitlements/current", async (
            HttpContext http,
            MuallimiDbContext db,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
            if (sub is null)
            {
                return Results.Ok(new
                {
                    plan_tier = "free",
                    feature_entitlements = Array.Empty<object>(),
                    usage_limits = Array.Empty<object>(),
                    is_grace_period = false,
                    grace_period_end = (DateTime?)null,
                });
            }
            var plan = await db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == sub.PlanId, ct);
            return Results.Ok(new
            {
                plan_tier = plan?.Tier ?? "free",
                feature_entitlements = ParseJson(plan?.FeatureEntitlements),
                usage_limits = ParseJson(plan?.UsageLimits),
                is_grace_period = sub.Status == "grace",
                grace_period_end = sub.GracePeriodEnd,
            });
        });

        // T109 — Saved payment method management (list / add / remove) through adapter.
        routes.MapGet("/api/v1/billing/payment-methods", async (
            HttpContext http,
            IPaymentMethodManagementService service,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var methods = await service.ListAsync(tenantId, ct);
            return Results.Ok(new
            {
                payment_methods = methods.Select(m => new
                {
                    method_ref = m.Ref,
                    type = m.Type,
                    masked_identifier = m.MaskedIdentifier,
                }),
            });
        });

        routes.MapPost("/api/v1/billing/payment-methods", async (
            HttpContext http,
            AddPaymentMethodRequestDto body,
            IPaymentMethodManagementService service,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.provider_token))
                return Results.BadRequest(new { error = "provider_token required" });
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
            var method = await service.AddAsync(tenantId, body.type ?? "card", body.provider_token, correlationId, ct);
            return Results.Created($"/api/v1/billing/payment-methods/{method.Ref}", new
            {
                method_ref = method.Ref,
                type = method.Type,
                masked_identifier = method.MaskedIdentifier,
            });
        });

        routes.MapDelete("/api/v1/billing/payment-methods/{methodRef}", async (
            string methodRef,
            HttpContext http,
            IPaymentMethodManagementService service,
            CancellationToken ct) =>
        {
            if (!TryTenant(http, out var tenantId)) return Results.Unauthorized();
            var correlationId = http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();
            await service.RemoveAsync(tenantId, methodRef, correlationId, ct);
            return Results.NoContent();
        });

        return routes;
    }

    // T109 — DTO for POST /api/v1/billing/payment-methods. snake_case to match the
    // existing billing endpoint convention.
    public sealed record AddPaymentMethodRequestDto(string? type, string provider_token);

    private static bool TryTenant(HttpContext http, out Guid tenantId)
    {
        var raw = http.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        return Guid.TryParse(raw, out tenantId);
    }

    private static Guid ResolveOperatorId(HttpContext http)
    {
        var raw = http.Request.Headers["X-Operator-Id"].FirstOrDefault();
        return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
    }

    private static Guid? ResolveSchoolTenantId(HttpContext http)
    {
        var raw = http.Request.Headers["X-School-Tenant-Id"].FirstOrDefault();
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static object? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); } catch { return null; }
    }

    private static readonly string[] ArabicIndicDigits = ["٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩"];

    private static string FormatCurrency(decimal amount, string currency, string locale)
    {
        var culture = locale == "ar" ? CultureInfo.GetCultureInfo("ar-EG") : CultureInfo.GetCultureInfo("en-US");
        var formatted = amount.ToString("N2", culture) + " " + currency.ToUpperInvariant();
        if (locale != "ar") return formatted;
        var sb = new StringBuilder(formatted.Length);
        foreach (var c in formatted) sb.Append(c is >= '0' and <= '9' ? ArabicIndicDigits[c - '0'] : c);
        return sb.ToString();
    }
}

public sealed record CreateSubscriptionBody(Guid PlanId, string? PaymentMethodRef);
public sealed record ChangePlanBody(Guid NewPlanId);
public sealed record CancelBody(string? Reason);
