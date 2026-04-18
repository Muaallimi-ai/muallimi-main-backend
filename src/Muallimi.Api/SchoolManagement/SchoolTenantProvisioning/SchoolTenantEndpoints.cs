using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

/// <summary>
/// T038 (US1) — POST <c>/operator/schools</c>.
///
/// Operator-authenticated endpoint. Creates a new school tenant and emits
/// a <c>school_created</c> downstream event in the same unit of work
/// (wired by <see cref="SchoolTenantProvisioningService"/>).
/// </summary>
public static class SchoolTenantEndpoints
{
    public const string Route = "/api/operator/schools";

    public sealed record CreateSchoolRequest(
        string school_name_ar,
        string school_name_en,
        string curriculum_type,
        int grade_range_start,
        int grade_range_end,
        List<string>? subject_bindings,
        JsonElement academic_calendar,
        string preferred_language);

    public static IEndpointRouteBuilder MapOperatorSchoolCreation(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleCreateAsync)
            .WithName("CreateSchoolTenant")
            .WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        CreateSchoolRequest body,
        ISchoolTenantProvisioningService service,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetOperatorActorId(http, out var operatorActorId))
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(body.school_name_ar)
            || string.IsNullOrWhiteSpace(body.school_name_en)
            || body.grade_range_end < body.grade_range_start)
        {
            return Results.BadRequest(new { error = "invalid_school_tenant_payload" });
        }

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var tenant = await service.CreateAsync(
            new SchoolTenantCreateInput(
                SchoolNameAr: body.school_name_ar,
                SchoolNameEn: body.school_name_en,
                CurriculumType: string.IsNullOrWhiteSpace(body.curriculum_type) ? "moe" : body.curriculum_type,
                GradeRangeStart: body.grade_range_start,
                GradeRangeEnd: body.grade_range_end,
                SubjectBindings: body.subject_bindings ?? new List<string>(),
                AcademicCalendar: body.academic_calendar,
                PreferredLanguage: string.IsNullOrWhiteSpace(body.preferred_language) ? "ar" : body.preferred_language,
                CreatedByOperatorId: operatorActorId,
                TenantId: tenantId),
            correlationId,
            ct);

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Created(
            uri: $"{Route}/{tenant.SchoolTenantId}",
            value: new
            {
                school_tenant_id = tenant.SchoolTenantId,
                tenant_id = tenant.TenantId,
                subscription_status = tenant.SubscriptionStatus,
                created_at = tenant.CreatedAt,
            });
    }
}
