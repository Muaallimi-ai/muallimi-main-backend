using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.Licensing;

/// <summary>
/// T019 — <c>LicensingEnforcementMiddleware</c>.
///
/// Enforces seat limits, license expiry, and feature gates on every
/// school-scoped request. Runs after <see cref="SchoolRoleClaimMiddleware"/>
/// so the active school tenant is available via
/// <see cref="ISchoolTenantResolver"/>.
///
/// Rules (see FR-024, FR-025 and US10 acceptance scenarios):
///   1. If subscription is <c>expired</c>, only GET requests to read-only
///      surfaces are allowed. All mutating requests return 402 with a
///      structured refusal code so the frontend can prompt a renewal.
///   2. If a feature gate is off, the corresponding endpoints return 403
///      with a structured refusal code.
///   3. Seat-limit enforcement runs in <see cref="RosterImportWorker"/>
///      (US2/T061) — this middleware only checks the license freshness.
///
/// The middleware is inert when no school tenant is resolved (e.g., operator
/// or public endpoints).
/// </summary>
public sealed class LicensingEnforcementMiddleware
{
    private readonly RequestDelegate _next;

    public LicensingEnforcementMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ISchoolTenantResolver tenantResolver,
        MuallimiDbContext db)
    {
        var schoolTenantId = await tenantResolver.ResolveAsync(context.RequestAborted);
        if (schoolTenantId is null)
        {
            await _next(context);
            return;
        }

        var license = await db.SchoolLicenses
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.SchoolTenantId == schoolTenantId.Value, context.RequestAborted);

        if (license is null)
        {
            // No license row = operator hasn't provisioned yet. Allow read-only
            // so the onboarding surfaces work; block mutating methods.
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await WriteRefusalAsync(context, 402, "license_missing", "License not provisioned for this school.");
                return;
            }
            await _next(context);
            return;
        }

        if (license.SubscriptionEnd <= DateTime.UtcNow)
        {
            if (!HttpMethods.IsGet(context.Request.Method))
            {
                await WriteRefusalAsync(context, 402, "license_expired", "School license has expired. Read-only mode.");
                return;
            }
        }

        // Feature-gate enforcement (US10 / T186). Path-prefix → feature-key map
        // keeps the middleware lightweight: endpoints that sit behind a gate
        // prefix their route with a well-known segment. Missing key ⇒ enabled
        // (fail-open for unknown features — operator must explicitly disable).
        var gated = ResolveGatedFeature(context.Request.Path);
        if (gated is not null && !IsFeatureEnabledInJson(license.FeatureGates, gated))
        {
            await WriteRefusalAsync(context, 403, "feature_gated", $"Feature '{gated}' is disabled for this school.");
            return;
        }

        await _next(context);
    }

    public static string? ResolveGatedFeature(PathString path)
    {
        if (!path.HasValue) return null;
        var value = path.Value!;
        if (value.Contains("/api/school-admin/exams", StringComparison.OrdinalIgnoreCase)) return "exams";
        if (value.Contains("/api/school-admin/announcements", StringComparison.OrdinalIgnoreCase)) return "announcements";
        if (value.Contains("/api/school-admin/reports", StringComparison.OrdinalIgnoreCase)) return "reports";
        if (value.Contains("/api/school-admin/leaderboards", StringComparison.OrdinalIgnoreCase)) return "leaderboards";
        return null;
    }

    public static bool IsFeatureEnabledInJson(string featureGatesJson, string featureKey)
    {
        if (string.IsNullOrWhiteSpace(featureGatesJson)) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(featureGatesJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return true;
            if (!doc.RootElement.TryGetProperty(featureKey, out var prop)) return true;
            return prop.ValueKind != System.Text.Json.JsonValueKind.False;
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    private static async Task WriteRefusalAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = $"{{\"refusal_code\":\"{code}\",\"message\":\"{message}\"}}";
        await context.Response.WriteAsync(body);
    }
}

public static class LicensingEnforcementMiddlewareApplicationBuilderExtensions
{
    public static IApplicationBuilder UseLicensingEnforcement(this IApplicationBuilder app)
    {
        return app.UseMiddleware<LicensingEnforcementMiddleware>();
    }
}
