using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.SchoolManagement.Licensing;

/// <summary>
/// T191 (US10) — operator license management endpoints.
///
/// Routes:
///   • GET  /api/operator/schools/{schoolId}/license  → single license
///   • PUT  /api/operator/schools/{schoolId}/license  → create/update license
///   • POST /api/operator/schools/{schoolId}/license/extend-trial
///                                                   → extend the trial end
///   • GET  /api/operator/licenses                    → list all licenses
///
/// These endpoints live outside the ambient school-tenant filter and require
/// <c>X-Operator-Actor-Id</c> + <c>X-Tenant-Id</c>.
/// </summary>
public static class OperatorLicenseEndpoints
{
    public const string SingleRoute = "/api/operator/schools/{schoolId:guid}/license";
    public const string ExtendTrialRoute = "/api/operator/schools/{schoolId:guid}/license/extend-trial";
    public const string ListRoute = "/api/operator/licenses";

    public sealed record PutRequest(
        string plan_tier,
        int seat_limit,
        string? feature_gates,
        DateTime subscription_start,
        DateTime subscription_end,
        bool is_trial);

    public sealed record ExtendTrialRequest(DateTime new_subscription_end);

    public static IEndpointRouteBuilder MapOperatorLicenses(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(SingleRoute, HandleGetAsync).WithName("GetOperatorSchoolLicense").WithTags("Licensing");
        routes.MapPut(SingleRoute, HandlePutAsync).WithName("PutOperatorSchoolLicense").WithTags("Licensing");
        routes.MapPost(ExtendTrialRoute, HandleExtendTrialAsync).WithName("ExtendOperatorSchoolLicenseTrial").WithTags("Licensing");
        routes.MapGet(ListRoute, HandleListAsync).WithName("ListOperatorLicenses").WithTags("Licensing");
        return routes;
    }

    public static async Task<IResult> HandleGetAsync(
        HttpContext http,
        Guid schoolId,
        ILicenseManagementService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetOperatorActorId(http, out _))
            return Results.Unauthorized();

        var license = await service.GetForOperatorAsync(schoolId, ct);
        if (license is null)
            return Results.NotFound(new { error = "license_not_found" });

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Ok(Project(license));
    }

    public static async Task<IResult> HandlePutAsync(
        HttpContext http,
        Guid schoolId,
        PutRequest request,
        ILicenseManagementService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetOperatorActorId(http, out _))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.BadRequest(new { error = "tenant_required" });
        if (request.seat_limit < 0)
            return Results.BadRequest(new { error = "invalid_seat_limit" });
        if (request.subscription_end <= request.subscription_start)
            return Results.BadRequest(new { error = "invalid_subscription_window" });

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var existing = await service.GetForOperatorAsync(schoolId, ct);
        if (existing is null)
        {
            var created = await service.CreateAsync(
                new LicenseCreateInput(
                    SchoolTenantId: schoolId,
                    TenantId: tenantId,
                    PlanTier: request.plan_tier,
                    SeatLimit: request.seat_limit,
                    FeatureGates: request.feature_gates ?? "{}",
                    SubscriptionStart: request.subscription_start,
                    SubscriptionEnd: request.subscription_end,
                    IsTrial: request.is_trial),
                correlationId,
                ct);

            http.Response.Headers["X-Correlation-Id"] = correlationId;
            return Results.Created($"/api/operator/schools/{schoolId}/license", Project(created));
        }

        var updated = await service.UpdateAsync(
            schoolId,
            new LicenseUpdateInput(
                PlanTier: request.plan_tier,
                SeatLimit: request.seat_limit,
                FeatureGates: request.feature_gates,
                SubscriptionEnd: request.subscription_end,
                IsTrial: request.is_trial),
            correlationId,
            ct);

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(Project(updated!));
    }

    public static async Task<IResult> HandleExtendTrialAsync(
        HttpContext http,
        Guid schoolId,
        ExtendTrialRequest request,
        ILicenseManagementService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetOperatorActorId(http, out _))
            return Results.Unauthorized();
        if (request.new_subscription_end <= DateTime.UtcNow)
            return Results.BadRequest(new { error = "invalid_trial_end" });

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var license = await service.ExtendTrialAsync(schoolId, request.new_subscription_end, correlationId, ct);
        if (license is null)
            return Results.NotFound(new { error = "license_not_found" });

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(Project(license));
    }

    public static async Task<IResult> HandleListAsync(
        HttpContext http,
        ILicenseManagementService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetOperatorActorId(http, out _))
            return Results.Unauthorized();

        var licenses = await service.ListForOperatorAsync(ct);
        var now = DateTime.UtcNow;
        var projected = new object[licenses.Count];
        for (var i = 0; i < licenses.Count; i++)
        {
            projected[i] = Project(licenses[i], now);
        }

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Ok(new { licenses = projected, total_count = licenses.Count });
    }

    private static object Project(Muallimi.Domain.SchoolManagement.SchoolLicense license, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        var seatsRemaining = Math.Max(0, license.SeatLimit - license.SeatsUsed);
        var daysRemaining = Math.Max(0, (int)(license.SubscriptionEnd - t).TotalDays);
        var expired = license.SubscriptionEnd <= t;
        return new
        {
            school_license_id = license.SchoolLicenseId,
            tenant_id = license.TenantId,
            school_tenant_id = license.SchoolTenantId,
            plan_tier = license.PlanTier,
            seat_limit = license.SeatLimit,
            seats_used = license.SeatsUsed,
            seats_remaining = seatsRemaining,
            feature_gates = ParseFeatureGates(license.FeatureGates),
            subscription_start = license.SubscriptionStart,
            subscription_end = license.SubscriptionEnd,
            is_trial = license.IsTrial,
            days_remaining = daysRemaining,
            expired,
            seat_warning_threshold = license.SeatWarningThreshold,
            updated_at = license.UpdatedAt,
        };
    }

    private static object? ParseFeatureGates(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new { };
        try { return JsonSerializer.Deserialize<object>(json); }
        catch (JsonException) { return new { }; }
    }
}
