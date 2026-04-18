using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Leaderboards.LeaderboardComputation;
using Muallimi.Api.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Leaderboards.LeaderboardQuery;

/// <summary>
/// T146 (US7) — role-scoped leaderboard query endpoints.
///
/// Routes:
///   • GET /api/school-admin/leaderboards/class/{classGroupId}
///       — admin view with full display names
///   • GET /api/teacher/leaderboards/class/{classGroupId}/subject/{subjectId}
///       — teacher view for their assigned subject (same as admin but
///       additionally scoped by TeacherAssignment)
///   • GET /api/student/leaderboard
///       — privacy-controlled view of the student's class leaderboard
///   • GET /api/parent/leaderboard/{childId}
///       — child-only rank + total, no sibling disclosure
///
/// Snapshots are pre-computed by
/// <see cref="LeaderboardComputationJob"/>; when no snapshot exists the
/// service computes one on demand so the contract test-path stays
/// deterministic without waiting for the background tick.
/// </summary>
public static class LeaderboardEndpoints
{
    public const string AdminClassRoute = "/api/school-admin/leaderboards/class/{classGroupId:guid}";
    public const string TeacherClassSubjectRoute = "/api/teacher/leaderboards/class/{classGroupId:guid}/subject/{subjectId:guid}";
    public const string StudentRoute = "/api/student/leaderboard";
    public const string ParentChildRoute = "/api/parent/leaderboard/{childId:guid}";

    public const string StudentHeaderName = "X-Student-Profile-Id";
    public const string ParentHeaderName = "X-Parent-Profile-Id";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEndpointRouteBuilder MapLeaderboardQueries(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(AdminClassRoute, HandleAdminClassAsync).WithName("GetAdminClassLeaderboard").WithTags("Leaderboards");
        routes.MapGet(TeacherClassSubjectRoute, HandleTeacherClassSubjectAsync).WithName("GetTeacherClassLeaderboard").WithTags("Leaderboards");
        routes.MapGet(StudentRoute, HandleStudentAsync).WithName("GetStudentLeaderboard").WithTags("Leaderboards");
        routes.MapGet(ParentChildRoute, HandleParentChildAsync).WithName("GetParentChildLeaderboard").WithTags("Leaderboards");
        return routes;
    }

    public static async Task<IResult> HandleAdminClassAsync(
        Guid classGroupId,
        HttpContext http,
        ILeaderboardSnapshotRepository snapshots,
        ILeaderboardComputationService compute,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetSchoolAdminId(http, out _))
        {
            return Results.Unauthorized();
        }

        var metric = ResolveMetric(http);
        var subjectId = ResolveSubjectId(http);
        var classGroup = await db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId
                && c.SchoolTenantId == schoolTenantId
                && c.ClassGroupId == classGroupId, ct);
        if (classGroup is null) return Results.NotFound(new { error = "class_not_found" });

        var snapshot = await ResolveSnapshotAsync(
            snapshots, compute, tenantId, schoolTenantId, classGroupId, metric, subjectId, ct);
        if (snapshot is null) return Results.NotFound(new { error = "leaderboard_disabled_or_empty" });

        return Results.Ok(new
        {
            class_group_id = classGroupId,
            display_name_ar = classGroup.DisplayNameAr,
            display_name_en = classGroup.DisplayNameEn,
            metric,
            window_start = snapshot.WindowStart,
            window_end = snapshot.WindowEnd,
            entries = ReadEntries(snapshot)
                .Select(e => new
                {
                    rank = e.rank,
                    student_id = e.student_id,
                    display_name_ar = e.real_display_name,
                    display_name_en = e.real_display_name,
                    value = e.value,
                })
                .ToList(),
            computed_at = snapshot.ComputedAt,
        });
    }

    public static async Task<IResult> HandleTeacherClassSubjectAsync(
        Guid classGroupId,
        Guid subjectId,
        HttpContext http,
        ILeaderboardSnapshotRepository snapshots,
        ILeaderboardComputationService compute,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetTeacherId(http, out var teacherId))
        {
            return Results.Unauthorized();
        }

        var assignment = await db.TeacherAssignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId
                && a.TeacherId == teacherId
                && a.ClassGroupId == classGroupId
                && a.SubjectId == subjectId
                && a.UnassignedAt == null, ct);
        if (assignment is null) return Results.StatusCode(403);

        var classGroup = await db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId
                && c.SchoolTenantId == schoolTenantId
                && c.ClassGroupId == classGroupId, ct);
        if (classGroup is null) return Results.NotFound(new { error = "class_not_found" });

        var metric = ResolveMetric(http);
        var snapshot = await ResolveSnapshotAsync(
            snapshots, compute, tenantId, schoolTenantId, classGroupId, metric, subjectId, ct);
        if (snapshot is null) return Results.NotFound(new { error = "leaderboard_disabled_or_empty" });

        return Results.Ok(new
        {
            class_group_id = classGroupId,
            subject_id = subjectId,
            display_name_ar = classGroup.DisplayNameAr,
            display_name_en = classGroup.DisplayNameEn,
            metric,
            window_start = snapshot.WindowStart,
            window_end = snapshot.WindowEnd,
            entries = ReadEntries(snapshot)
                .Select(e => new
                {
                    rank = e.rank,
                    student_id = e.student_id,
                    display_name_ar = e.real_display_name,
                    display_name_en = e.real_display_name,
                    value = e.value,
                })
                .ToList(),
            computed_at = snapshot.ComputedAt,
        });
    }

    public static async Task<IResult> HandleStudentAsync(
        HttpContext http,
        ILeaderboardSnapshotRepository snapshots,
        ILeaderboardComputationService compute,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetStudentProfileId(http, out var studentId))
        {
            return Results.Unauthorized();
        }

        var enrolment = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.StudentId == studentId && e.Status == "active")
            .FirstOrDefaultAsync(ct);
        if (enrolment is null) return Results.NotFound(new { error = "no_active_enrolment" });

        var classGroup = await db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClassGroupId == enrolment.ClassGroupId, ct);
        if (classGroup is null) return Results.NotFound(new { error = "class_not_found" });

        var metric = ResolveMetric(http);
        var subjectId = ResolveSubjectId(http);
        var snapshot = await ResolveSnapshotAsync(
            snapshots, compute, tenantId, classGroup.SchoolTenantId, enrolment.ClassGroupId, metric, subjectId, ct);
        if (snapshot is null) return Results.NotFound(new { error = "leaderboard_disabled_or_empty" });

        var entries = ReadEntries(snapshot);
        var self = entries.FirstOrDefault(e => e.student_id == studentId);

        return Results.Ok(new
        {
            class_group_id = enrolment.ClassGroupId,
            metric,
            my_rank = self?.rank ?? 0,
            my_value = self?.value ?? 0m,
            entries = entries.Select(e => new
            {
                rank = e.rank,
                display_name = e.display_name,
                value = e.value,
                is_self = e.student_id == studentId,
            }).ToList(),
            computed_at = snapshot.ComputedAt,
        });
    }

    public static async Task<IResult> HandleParentChildAsync(
        Guid childId,
        HttpContext http,
        ILeaderboardSnapshotRepository snapshots,
        ILeaderboardComputationService compute,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !TryGetParentProfileId(http, out var parentProfileId))
        {
            return Results.Unauthorized();
        }

        var today = DateTime.UtcNow.Date;
        var link = await db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId
                && l.ParentProfileId == parentProfileId
                && l.StudentId == childId
                && l.EffectiveStart <= today
                && (l.EffectiveEnd == null || l.EffectiveEnd >= today), ct);
        if (link is null) return Results.StatusCode(403);

        var enrolment = await db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.StudentId == childId && e.Status == "active", ct);
        if (enrolment is null) return Results.NotFound(new { error = "no_active_enrolment" });

        var classGroup = await db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ClassGroupId == enrolment.ClassGroupId, ct);
        if (classGroup is null) return Results.NotFound(new { error = "class_not_found" });

        var metric = ResolveMetric(http);
        var subjectId = ResolveSubjectId(http);
        var snapshot = await ResolveSnapshotAsync(
            snapshots, compute, tenantId, classGroup.SchoolTenantId, enrolment.ClassGroupId, metric, subjectId, ct);
        if (snapshot is null) return Results.NotFound(new { error = "leaderboard_disabled_or_empty" });

        var entries = ReadEntries(snapshot);
        var child = entries.FirstOrDefault(e => e.student_id == childId);
        if (child is null) return Results.NotFound(new { error = "child_not_ranked" });

        return Results.Ok(new
        {
            child_id = childId,
            class_group_id = enrolment.ClassGroupId,
            metric,
            rank = child.rank,
            total_students = entries.Count,
            value = child.value,
            computed_at = snapshot.ComputedAt,
        });
    }

    private static async Task<LeaderboardSnapshot?> ResolveSnapshotAsync(
        ILeaderboardSnapshotRepository snapshots,
        ILeaderboardComputationService compute,
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        string metric,
        Guid? subjectId,
        CancellationToken ct)
    {
        var existing = await snapshots.GetLatestAsync(
            tenantId, schoolTenantId, "class", classGroupId, metric, subjectId, ct);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        return await compute.ComputeClassSnapshotAsync(
            tenantId, schoolTenantId, classGroupId, metric, subjectId,
            now.AddDays(-7), now, ct);
    }

    private static List<LeaderboardComputationService.LeaderboardEntryPayload> ReadEntries(LeaderboardSnapshot snapshot)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<List<LeaderboardComputationService.LeaderboardEntryPayload>>(
                snapshot.Entries, JsonOptions);
            return parsed ?? new List<LeaderboardComputationService.LeaderboardEntryPayload>();
        }
        catch
        {
            return new List<LeaderboardComputationService.LeaderboardEntryPayload>();
        }
    }

    private static string ResolveMetric(HttpContext http)
    {
        var raw = http.Request.Query["metric"].ToString();
        if (!string.IsNullOrWhiteSpace(raw) && LeaderboardMetrics.IsValid(raw)) return raw;
        return LeaderboardMetrics.Mastery;
    }

    private static Guid? ResolveSubjectId(HttpContext http)
    {
        var raw = http.Request.Query["subject_id"].ToString();
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static bool TryGetStudentProfileId(HttpContext http, out Guid studentId)
        => Guid.TryParse(http.Request.Headers[StudentHeaderName].ToString(), out studentId);

    private static bool TryGetParentProfileId(HttpContext http, out Guid parentProfileId)
        => Guid.TryParse(http.Request.Headers[ParentHeaderName].ToString(), out parentProfileId);
}
