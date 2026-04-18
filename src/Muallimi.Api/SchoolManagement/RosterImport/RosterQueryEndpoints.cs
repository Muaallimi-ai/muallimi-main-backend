using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T059 (US2) — roster import status + student-list read surfaces.
///
/// Three endpoints:
///   • GET <c>/school-admin/roster/imports/{id}</c> — returns the status
///     + counts for an import job so the frontend can poll.
///   • GET <c>/school-admin/roster/imports/{id}/errors</c> — returns the
///     error-report CSV for download.
///   • GET <c>/school-admin/roster/students</c> — paginated list of
///     students in the school, with Arabic / English name search and
///     grade + class filters.
///
/// Every query is scoped by (tenant_id, school_tenant_id) read from the
/// request headers — cross-school reads are impossible.
/// </summary>
public static class RosterQueryEndpoints
{
    public const string StatusRoute = "/api/school-admin/roster/imports/{rosterImportId:guid}";
    public const string ErrorsRoute = "/api/school-admin/roster/imports/{rosterImportId:guid}/errors";
    public const string StudentsRoute = "/api/school-admin/roster/students";

    public static IEndpointRouteBuilder MapRosterImportQueries(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(StatusRoute, HandleStatusAsync).WithName("GetRosterImportStatus").WithTags("SchoolManagement");
        routes.MapGet(ErrorsRoute, HandleErrorsAsync).WithName("GetRosterImportErrors").WithTags("SchoolManagement");
        routes.MapGet(StudentsRoute, HandleStudentsAsync).WithName("ListRosterStudents").WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleStatusAsync(
        Guid rosterImportId,
        HttpContext http,
        IRosterImportRepository repo,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        var row = await repo.GetByIdAsync(tenantId, schoolTenantId, rosterImportId, ct);
        if (row is null) return Results.NotFound(new { error = "roster_import_not_found" });

        return Results.Ok(new
        {
            roster_import_id = row.RosterImportId,
            status = row.Status,
            total_row_count = row.TotalRowCount,
            success_count = row.SuccessCount,
            error_count = row.ErrorCount,
            skip_count = row.SkipCount,
            error_report_url = row.ErrorReportBlobKey is null ? null : $"/api/school-admin/roster/imports/{row.RosterImportId}/errors",
            started_at = row.StartedAt,
            completed_at = row.CompletedAt,
            created_at = row.CreatedAt,
        });
    }

    public static async Task<IResult> HandleErrorsAsync(
        Guid rosterImportId,
        HttpContext http,
        IRosterImportRepository repo,
        IRosterFileStore files,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        var row = await repo.GetByIdAsync(tenantId, schoolTenantId, rosterImportId, ct);
        if (row is null) return Results.NotFound(new { error = "roster_import_not_found" });
        if (row.ErrorReportBlobKey is null) return Results.NoContent();

        var bytes = await files.TryReadAsync(row.ErrorReportBlobKey, ct);
        if (bytes is null) return Results.NotFound(new { error = "error_report_missing" });

        return Results.File(bytes, contentType: "text/csv; charset=utf-8", fileDownloadName: $"roster-errors-{rosterImportId}.csv");
    }

    public static async Task<IResult> HandleStudentsAsync(
        HttpContext http,
        MuallimiDbContext db,
        CancellationToken ct,
        int? page,
        int? page_size,
        string? search,
        int? grade,
        Guid? class_group_id)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        var pageValue = Math.Max(1, page ?? 1);
        var pageSize = Math.Clamp(page_size ?? 50, 1, 200);

        // Students belong to the school's tenant; we scope by tenant_id and
        // (when class_group_id is supplied) filter through ClassEnrolment so
        // cross-class filters respect the school boundary even before US3
        // ships class management.
        var studentsQuery = db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId);

        if (grade.HasValue)
        {
            var gradeStr = grade.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            studentsQuery = studentsQuery.Where(s => s.Grade == gradeStr);
        }

        if (class_group_id.HasValue)
        {
            var enrolmentIds = db.ClassEnrolments
                .IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId
                            && e.ClassGroupId == class_group_id.Value
                            && e.Status == "active")
                .Select(e => e.StudentId);
            studentsQuery = studentsQuery.Where(s => enrolmentIds.Contains(s.Id));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            studentsQuery = studentsQuery.Where(s =>
                EF.Functions.Like(s.DisplayName, $"%{q}%"));
        }

        var total = await studentsQuery.CountAsync(ct);
        var raw = await studentsQuery
            .OrderBy(s => s.DisplayName)
            .Skip((pageValue - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = new List<object>(raw.Count);
        foreach (var s in raw)
        {
            var parentLinked = await db.ChildLinks
                .IgnoreQueryFilters()
                .AnyAsync(l => l.TenantId == tenantId && l.StudentId == s.Id, ct);
            var classes = await db.ClassEnrolments
                .IgnoreQueryFilters()
                .Where(e => e.TenantId == tenantId && e.StudentId == s.Id && e.Status == "active")
                .Select(e => e.ClassGroupId.ToString())
                .ToListAsync(ct);

            int.TryParse(s.Grade, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var gradeInt);
            items.Add(new
            {
                student_id = s.Id,
                display_name_ar = s.DisplayName,
                display_name_en = s.DisplayName,
                grade = gradeInt,
                class_groups = classes,
                enrolment_status = classes.Count == 0 ? "not_enrolled" : "active",
                parent_linked = parentLinked,
            });
        }

        return Results.Ok(new { students = items, total_count = total, page = pageValue, page_size = pageSize });
    }

    private static bool TryResolveScope(HttpContext http, out Guid tenantId, out Guid schoolTenantId)
    {
        schoolTenantId = Guid.Empty;
        return SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
               && SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId)
               && SchoolManagementHeaders.TryGetSchoolAdminId(http, out _);
    }
}
