using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement.AdminOnboarding;

namespace Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

/// <summary>
/// T040 (US1) — GET / PUT <c>/school-admin/school</c>.
///
/// Returns the authenticated school admin's school configuration. The
/// admin identity is resolved from the <c>X-School-Admin-Id</c> header
/// (local-parity stub); production wires the resolver to
/// <see cref="SchoolTenantResolver"/> claims. Tenant isolation is
/// enforced by looking up the admin row under the ambient tenant id
/// before surfacing the school tenant.
/// </summary>
public static class SchoolConfigEndpoints
{
    public const string Route = "/api/school-admin/school";

    public sealed record UpdateSchoolConfigRequest(
        List<string>? subject_bindings,
        JsonElement? academic_calendar,
        string? preferred_language);

    public static IEndpointRouteBuilder MapSchoolAdminConfig(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleGetAsync)
            .WithName("GetSchoolConfig")
            .WithTags("SchoolManagement");
        routes.MapPut(Route, HandleUpdateAsync)
            .WithName("UpdateSchoolConfig")
            .WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleGetAsync(
        HttpContext http,
        ISchoolTenantProvisioningService service,
        ISchoolAdminRepository admins,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetSchoolAdminId(http, out var schoolAdminId))
            return Results.Unauthorized();

        var admin = await admins.GetByIdAsync(tenantId, schoolAdminId, ct);
        if (admin is null || admin.OnboardingStatus != "onboarded")
            return Results.NotFound();

        var tenant = await service.GetConfigurationAsync(tenantId, admin.SchoolTenantId, ct);
        if (tenant is null) return Results.NotFound();

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Ok(new
        {
            school_tenant_id = tenant.SchoolTenantId,
            school_name_ar = tenant.SchoolNameAr,
            school_name_en = tenant.SchoolNameEn,
            curriculum_type = tenant.CurriculumType,
            grade_range_start = tenant.GradeRangeStart,
            grade_range_end = tenant.GradeRangeEnd,
            subject_bindings = DeserializeArray(tenant.SubjectBindings),
            academic_calendar = DeserializeObject(tenant.AcademicCalendar),
            preferred_language = tenant.PreferredLanguage,
            subscription_status = tenant.SubscriptionStatus,
        });
    }

    public static async Task<IResult> HandleUpdateAsync(
        HttpContext http,
        UpdateSchoolConfigRequest body,
        ISchoolTenantProvisioningService service,
        ISchoolAdminRepository admins,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetSchoolAdminId(http, out var schoolAdminId))
            return Results.Unauthorized();

        var admin = await admins.GetByIdAsync(tenantId, schoolAdminId, ct);
        if (admin is null || admin.OnboardingStatus != "onboarded")
            return Results.NotFound();

        var tenant = await service.UpdateConfigurationAsync(
            tenantId,
            admin.SchoolTenantId,
            new SchoolTenantUpdateInput(
                SubjectBindings: body.subject_bindings,
                AcademicCalendar: body.academic_calendar,
                PreferredLanguage: body.preferred_language),
            ct);
        if (tenant is null) return Results.NotFound();

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Ok(new
        {
            school_tenant_id = tenant.SchoolTenantId,
            subject_bindings = DeserializeArray(tenant.SubjectBindings),
            academic_calendar = DeserializeObject(tenant.AcademicCalendar),
            preferred_language = tenant.PreferredLanguage,
            updated_at = tenant.UpdatedAt,
        });
    }

    private static JsonElement DeserializeObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static List<string> DeserializeArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}
