using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Billing.EntitlementEnforcement;

/// <summary>
/// T015 — Evaluates tenant subscription status, plan entitlements, usage
/// limits, and feature flags on every authenticated request. Integrates with
/// Phase 5 LicensingEnforcementMiddleware for school tenants. Full policy
/// logic lands in US1; the middleware here resolves the current subscription
/// and attaches it to HttpContext.Items so downstream handlers can consult it.
/// </summary>
public class EntitlementEnforcementMiddleware
{
    public const string HttpItemKey = "Phase6.EntitlementSnapshot";

    private readonly RequestDelegate _next;

    public EntitlementEnforcementMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, MuallimiDbContext db)
    {
        var tenantIdHeader = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (Guid.TryParse(tenantIdHeader, out var tenantId))
        {
            var sub = await db.Subscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId);

            context.Items[HttpItemKey] = new EntitlementSnapshot(
                TenantId: tenantId,
                SubscriptionStatus: sub?.Status,
                PlanId: sub?.PlanId);
        }

        await _next(context);
    }
}

public record EntitlementSnapshot(
    Guid TenantId,
    string? SubscriptionStatus,
    Guid? PlanId);

public static class EntitlementEnforcementExtensions
{
    public static IApplicationBuilder UsePhase6EntitlementEnforcement(this IApplicationBuilder app)
        => app.UseMiddleware<EntitlementEnforcementMiddleware>();
}
