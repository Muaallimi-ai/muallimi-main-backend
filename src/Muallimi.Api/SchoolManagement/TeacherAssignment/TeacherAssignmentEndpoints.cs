using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.TeacherAssignment;

/// <summary>
/// T078 (US3) — teacher and teacher-assignment endpoints.
///
/// Routes:
///   • GET    /api/school-admin/teachers
///   • POST   /api/school-admin/classes/{classGroupId}/teachers
///   • DELETE /api/school-admin/classes/{classGroupId}/teachers/{teacherAssignmentId}
///   • GET    /api/school-admin/teachers/{teacherId}/assignments → for scope
///     verification (role-isolation test, teacher-dashboard preview)
/// </summary>
public static class TeacherAssignmentEndpoints
{
    public const string ListTeachersRoute = "/api/school-admin/teachers";
    public const string AssignRoute = "/api/school-admin/classes/{classGroupId:guid}/teachers";
    public const string UnassignRoute = "/api/school-admin/classes/{classGroupId:guid}/teachers/{teacherAssignmentId:guid}";
    public const string TeacherAssignmentsRoute = "/api/school-admin/teachers/{teacherId:guid}/assignments";

    public sealed record AssignTeacherRequest(Guid teacher_id, Guid subject_id);

    public static IEndpointRouteBuilder MapTeacherAssignments(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ListTeachersRoute, HandleListTeachersAsync).WithName("ListTeachers").WithTags("SchoolManagement");
        routes.MapPost(AssignRoute, HandleAssignAsync).WithName("AssignTeacher").WithTags("SchoolManagement");
        routes.MapDelete(UnassignRoute, HandleUnassignAsync).WithName("UnassignTeacher").WithTags("SchoolManagement");
        routes.MapGet(TeacherAssignmentsRoute, HandleTeacherAssignmentsAsync).WithName("GetTeacherAssignments").WithTags("SchoolManagement");
        return routes;
    }

    public static async Task<IResult> HandleListTeachersAsync(
        HttpContext http,
        ITeacherRepository teachers,
        ITeacherAssignmentRepository assignments,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        var rows = await teachers.ListForSchoolAsync(tenantId, schoolTenantId, ct);
        var items = new List<object>(rows.Count);
        foreach (var t in rows)
        {
            var scopes = await assignments.ListActiveForTeacherAsync(tenantId, t.TeacherId, ct);
            items.Add(new
            {
                teacher_id = t.TeacherId,
                display_name_ar = t.DisplayNameAr,
                display_name_en = t.DisplayNameEn,
                assigned_class_count = scopes.Select(s => s.ClassGroupId).Distinct().Count(),
                subject_ids = scopes.Select(s => s.SubjectId).Distinct(),
            });
        }

        return Results.Ok(new { teachers = items, total_count = rows.Count });
    }

    public static async Task<IResult> HandleAssignAsync(
        Guid classGroupId,
        HttpContext http,
        AssignTeacherRequest body,
        ITeacherAssignmentService service,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();
        if (body.teacher_id == Guid.Empty || body.subject_id == Guid.Empty)
            return Results.BadRequest(new { error = "teacher_and_subject_required" });

        try
        {
            var row = await service.AssignAsync(new TeacherAssignmentInput(
                TenantId: tenantId,
                SchoolTenantId: schoolTenantId,
                ClassGroupId: classGroupId,
                TeacherId: body.teacher_id,
                SubjectId: body.subject_id), ct);

            return Results.Created(
                uri: $"/api/school-admin/classes/{classGroupId}/teachers/{row.TeacherAssignmentId}",
                value: new
                {
                    teacher_assignment_id = row.TeacherAssignmentId,
                    teacher_id = row.TeacherId,
                    class_group_id = row.ClassGroupId,
                    subject_id = row.SubjectId,
                    assigned_at = row.AssignedAt,
                });
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    public static async Task<IResult> HandleUnassignAsync(
        Guid classGroupId,
        Guid teacherAssignmentId,
        HttpContext http,
        ITeacherAssignmentService service,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, out var tenantId, out var schoolTenantId)) return Results.Unauthorized();

        try
        {
            var removed = await service.UnassignAsync(tenantId, schoolTenantId, classGroupId, teacherAssignmentId, ct);
            return removed
                ? Results.Ok(new { unassigned = true, teacher_assignment_id = teacherAssignmentId })
                : Results.NotFound(new { error = "assignment_not_found" });
        }
        catch (InvalidOperationException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Returns the active assignment scopes for a teacher — used by the
    /// role-isolation contract test and by the future teacher dashboard.
    /// The caller identifies as the teacher via X-Teacher-Id; admins can
    /// also read this (admin header path) for the teacher management UI.
    /// </summary>
    public static async Task<IResult> HandleTeacherAssignmentsAsync(
        Guid teacherId,
        HttpContext http,
        ITeacherAssignmentRepository assignments,
        ITeacherRepository teachers,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId))
            return Results.Unauthorized();

        // Either the school admin header OR the teacher acting as self.
        var isAdmin = SchoolManagementHeaders.TryGetSchoolAdminId(http, out _);
        var isSelfTeacher = Guid.TryParse(http.Request.Headers["X-Teacher-Id"].ToString(), out var callerTeacherId)
            && callerTeacherId == teacherId;
        if (!isAdmin && !isSelfTeacher) return Results.Unauthorized();

        var teacher = await teachers.GetByIdAsync(tenantId, schoolTenantId, teacherId, ct);
        if (teacher is null) return Results.NotFound(new { error = "teacher_not_found" });

        var scopes = await assignments.ListActiveForTeacherAsync(tenantId, teacherId, ct);
        var classIds = scopes.Select(s => s.ClassGroupId).ToHashSet();
        var classes = await db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.SchoolTenantId == schoolTenantId && classIds.Contains(c.ClassGroupId))
            .ToListAsync(ct);
        var classMap = classes.ToDictionary(c => c.ClassGroupId, c => c);

        return Results.Ok(new
        {
            teacher_id = teacher.TeacherId,
            display_name_ar = teacher.DisplayNameAr,
            display_name_en = teacher.DisplayNameEn,
            assignments = scopes.Select(s =>
            {
                classMap.TryGetValue(s.ClassGroupId, out var c);
                return new
                {
                    teacher_assignment_id = s.TeacherAssignmentId,
                    class_group_id = s.ClassGroupId,
                    display_name_ar = c?.DisplayNameAr ?? string.Empty,
                    display_name_en = c?.DisplayNameEn ?? string.Empty,
                    grade = c?.Grade ?? 0,
                    section_label = c?.SectionLabel ?? string.Empty,
                    subject_id = s.SubjectId,
                    assigned_at = s.AssignedAt,
                };
            }),
        });
    }

    private static bool TryResolveScope(HttpContext http, out Guid tenantId, out Guid schoolTenantId)
    {
        schoolTenantId = Guid.Empty;
        return SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
               && SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId)
               && SchoolManagementHeaders.TryGetSchoolAdminId(http, out _);
    }
}
