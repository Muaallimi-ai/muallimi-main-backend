using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.SchoolManagement.Licensing;

/// <summary>
/// T190 (US10) — GET <c>/school-admin/license</c>.
///
/// Returns the school's current license details including seat usage,
/// feature gates, subscription period, and warning state.
/// </summary>
public static class LicenseStatusEndpoints
{
    public const string Route = "/api/school-admin/license";

    public static IEndpointRouteBuilder MapLicenseStatus(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleGetAsync)
            .WithName("GetSchoolLicense")
            .WithTags("Licensing");
        return routes;
    }

    public static async Task<IResult> HandleGetAsync(
        HttpContext http,
        ILicenseManagementService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId))
            return Results.Unauthorized();

        var license = await service.GetForSchoolAdminAsync(tenantId, schoolTenantId, ct);
        if (license is null)
            return Results.NotFound(new { error = "license_not_found" });

        var now = DateTime.UtcNow;
        var seatsRemaining = Math.Max(0, license.SeatLimit - license.SeatsUsed);
        var daysRemaining = Math.Max(0, (int)(license.SubscriptionEnd - now).TotalDays);
        var seatWarning = license.SeatLimit > 0
            && (double)license.SeatsUsed / license.SeatLimit * 100 >= license.SeatWarningThreshold;

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Ok(new
        {
            school_license_id = license.SchoolLicenseId,
            plan_tier = license.PlanTier,
            seat_limit = license.SeatLimit,
            seats_used = license.SeatsUsed,
            seats_remaining = seatsRemaining,
            feature_gates = JsonSerializer.Deserialize<object>(license.FeatureGates),
            subscription_start = license.SubscriptionStart,
            subscription_end = license.SubscriptionEnd,
            is_trial = license.IsTrial,
            days_remaining = daysRemaining,
            seat_warning = seatWarning,
        });
    }
}
