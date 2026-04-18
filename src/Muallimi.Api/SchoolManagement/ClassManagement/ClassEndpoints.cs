using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.ClassManagement;

/// <summary>
/// T076 (US3) — POST/GET class endpoints.
///
/// Routes:
///   • POST /api/school-admin/classes                  → create class
///   • GET  /api/school-admin/classes                  → list classes
///   • GET  /api/school-admin/classes/{classGroupId}   → detail with
///       enrolled students and assigned teachers
///
/// Every request is tenant + school-tenant + admin scoped via headers.
/// The detail response joins StudentProfiles (for display names) and
/// Teachers (for teacher names) without ever projecting forbidden
/// columns (billing, family email, etc.).
/// </summary>
public static class ClassEndpoints
{
    public const string CreateRoute = "/api/school-admin/classes";
    public const string ListRoute = "/api/school-admin/classes";
    public const string DetailRoute = "/api/school-admin/classes/{classGroupId:guid}";

    public sealed record CreateClassRequest(
        int grade,
        string section_label,
        string display_name_ar,
        string display_name_en,
        List<string>? subject_bindings,
        string academic_year);

    public static IEndpointRouteBuilder MapClassManagement(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(CreateRoute, HandleCreateAsync).WithName("CreateClassGroup").WithTags("SchoolManagement");
        routes.MapGet(ListRoute, HandleListAsync).WithName("ListClassGroups").WithTags("SchoolManagement");
        routes.MapGet(DetailRoute, HandleDetailAsync).WithName("GetClassGroupDetail").WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        CreateClassRequest body,
        IClassManagementService service,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();
        if (body.grade <= 0 || string.IsNullOrWhiteSpace(body.display_name_ar) || string.IsNullOrWhiteSpace(body.display_name_en))
            return Results.BadRequest(new { error = "invalid_class_payload" });

        var row = await service.CreateClassAsync(new ClassGroupCreateInput(
            Grade: body.grade,
            SectionLabel: body.section_label ?? string.Empty,
            DisplayNameAr: body.display_name_ar,
            DisplayNameEn: body.display_name_en,
            SubjectBindings: body.subject_bindings ?? new List<string>(),
            AcademicYear: body.academic_year ?? string.Empty,
            TenantId: tenantId,
            SchoolTenantId: schoolTenantId), ct);

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Created(
            uri: $"{CreateRoute}/{row.ClassGroupId}",
            value: new
            {
                class_group_id = row.ClassGroupId,
                display_name_ar = row.DisplayNameAr,
                display_name_en = row.DisplayNameEn,
                grade = row.Grade,
                section_label = row.SectionLabel,
                academic_year = row.AcademicYear,
                created_at = row.CreatedAt,
            });
    }

    public static async Task<IResult> HandleListAsync(
        HttpContext http,
        IClassGroupRepository repo,
        IClassEnrolmentRepository enrolments,
        ITeacherAssignmentRepository assignments,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        var classes = await repo.ListForSchoolAsync(tenantId, schoolTenantId, ct);
        var items = new List<object>(classes.Count);
        foreach (var c in classes)
        {
            var enrolled = await enrolments.ListActiveForClassAsync(tenantId, c.ClassGroupId, ct);
            var teachers = await assignments.ListForClassAsync(tenantId, c.ClassGroupId, ct);
            items.Add(new
            {
                class_group_id = c.ClassGroupId,
                grade = c.Grade,
                section_label = c.SectionLabel,
                display_name_ar = c.DisplayNameAr,
                display_name_en = c.DisplayNameEn,
                subject_bindings = DeserializeArray(c.SubjectBindings),
                academic_year = c.AcademicYear,
                student_count = enrolled.Count,
                teacher_count = teachers.Count,
            });
        }

        return Results.Ok(new { classes = items, total_count = classes.Count });
    }

    public static async Task<IResult> HandleDetailAsync(
        Guid classGroupId,
        HttpContext http,
        IClassGroupRepository repo,
        IClassEnrolmentRepository enrolments,
        ITeacherAssignmentRepository assignments,
        ITeacherRepository teachers,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        var c = await repo.GetByIdAsync(tenantId, schoolTenantId, classGroupId, ct);
        if (c is null) return Results.NotFound(new { error = "class_not_found" });

        var activeEnrolments = await enrolments.ListActiveForClassAsync(tenantId, classGroupId, ct);
        var studentIds = activeEnrolments.Select(e => e.StudentId).ToHashSet();

        var students = await db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.DisplayName })
            .ToListAsync(ct);

        var teacherAssignments = await assignments.ListForClassAsync(tenantId, classGroupId, ct);
        var teacherIds = teacherAssignments.Select(a => a.TeacherId).ToHashSet();
        var teacherRows = await db.Teachers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId && teacherIds.Contains(t.TeacherId))
            .ToListAsync(ct);
        var teacherMap = teacherRows.ToDictionary(t => t.TeacherId, t => t);

        return Results.Ok(new
        {
            class_group_id = c.ClassGroupId,
            grade = c.Grade,
            section_label = c.SectionLabel,
            display_name_ar = c.DisplayNameAr,
            display_name_en = c.DisplayNameEn,
            subject_bindings = DeserializeArray(c.SubjectBindings),
            academic_year = c.AcademicYear,
            enrolled_students = students.Select(s => new
            {
                student_id = s.Id,
                display_name_ar = s.DisplayName,
                display_name_en = s.DisplayName,
                enrolment_status = "active",
            }),
            assigned_teachers = teacherAssignments.Select(a =>
            {
                teacherMap.TryGetValue(a.TeacherId, out var t);
                return new
                {
                    teacher_assignment_id = a.TeacherAssignmentId,
                    teacher_id = a.TeacherId,
                    display_name_ar = t?.DisplayNameAr ?? string.Empty,
                    display_name_en = t?.DisplayNameEn ?? string.Empty,
                    subject_id = a.SubjectId,
                    assigned_at = a.AssignedAt,
                };
            }),
            student_count = students.Count,
        });
    }

    private static bool TryResolveScope(HttpContext http, out Guid tenantId, out Guid schoolTenantId)
    {
        schoolTenantId = Guid.Empty;
        return SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
               && SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId)
               && SchoolManagementHeaders.TryGetSchoolAdminId(http, out _);
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
