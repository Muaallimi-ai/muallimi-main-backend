using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.SchoolManagement.ClassManagement;

/// <summary>
/// T077 (US3) — enrolment and transfer endpoints.
///
/// Routes:
///   • POST /api/school-admin/classes/{classGroupId}/enrolments → bulk enrol
///   • POST /api/school-admin/classes/{classGroupId}/transfers  → transfer
///   • DELETE /api/school-admin/classes/{classGroupId}/enrolments/{studentId} → unenrol
/// </summary>
public static class EnrolmentEndpoints
{
    public const string EnrolRoute = "/api/school-admin/classes/{classGroupId:guid}/enrolments";
    public const string TransferRoute = "/api/school-admin/classes/{classGroupId:guid}/transfers";
    public const string UnenrolRoute = "/api/school-admin/classes/{classGroupId:guid}/enrolments/{studentId:guid}";

    public sealed record EnrolRequest(List<Guid> student_ids);

    public sealed record TransferRequest(Guid student_id, Guid target_class_group_id);

    public static IEndpointRouteBuilder MapEnrolments(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(EnrolRoute, HandleEnrolAsync).WithName("EnrolStudents").WithTags("SchoolManagement");
        routes.MapPost(TransferRoute, HandleTransferAsync).WithName("TransferStudent").WithTags("SchoolManagement");
        routes.MapDelete(UnenrolRoute, HandleUnenrolAsync).WithName("UnenrolStudent").WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleEnrolAsync(
        Guid classGroupId,
        HttpContext http,
        EnrolRequest body,
        IClassManagementService service,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();
        if (body.student_ids is null || body.student_ids.Count == 0)
            return Results.BadRequest(new { error = "student_ids_required" });

        try
        {
            var outcome = await service.EnrolStudentsAsync(tenantId, schoolTenantId, classGroupId, body.student_ids, ct);
            return Results.Ok(new
            {
                enrolled_count = outcome.EnrolledCount,
                already_enrolled_count = outcome.AlreadyEnrolledCount,
                enrolled_student_ids = outcome.StudentIds,
            });
        }
        catch (InvalidOperationException ex) when (ex.Message == "class_not_found")
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    public static async Task<IResult> HandleTransferAsync(
        Guid classGroupId,
        HttpContext http,
        TransferRequest body,
        IClassManagementService service,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();
        if (body.student_id == Guid.Empty || body.target_class_group_id == Guid.Empty)
            return Results.BadRequest(new { error = "student_id_and_target_required" });

        try
        {
            var outcome = await service.TransferStudentAsync(tenantId, schoolTenantId, classGroupId, body.target_class_group_id, body.student_id, ct);
            if (!outcome.Transferred)
                return Results.NotFound(new { error = "student_not_enrolled_in_source" });

            return Results.Ok(new
            {
                transferred = true,
                new_enrolment_id = outcome.NewEnrolmentId,
                source_class_group_id = classGroupId,
                target_class_group_id = body.target_class_group_id,
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    public static async Task<IResult> HandleUnenrolAsync(
        Guid classGroupId,
        Guid studentId,
        HttpContext http,
        IClassManagementService service,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        try
        {
            var removed = await service.UnenrolStudentAsync(tenantId, schoolTenantId, classGroupId, studentId, ct);
            return removed
                ? Results.Ok(new { unenrolled = true })
                : Results.NotFound(new { error = "enrolment_not_found" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static bool TryResolveScope(HttpContext http, out Guid tenantId, out Guid schoolTenantId)
    {
        schoolTenantId = Guid.Empty;
        return SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
               && SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId)
               && SchoolManagementHeaders.TryGetSchoolAdminId(http, out _);
    }
}
