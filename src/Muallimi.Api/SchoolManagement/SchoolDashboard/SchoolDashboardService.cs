using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.SchoolDashboard;

/// <summary>
/// T090 (US4) — <c>SchoolDashboardService</c>.
///
/// Aggregates school-wide and class-level metrics from
/// <see cref="SchoolAggregateView"/> rows (pre-rolled from Phase 4 downstream
/// events) and the live Phase 4 per-student tables (MasteryState, StreakState,
/// BadgeAward, FocusArea, AtRiskFlag) for class drill-down views.
///
/// Hot paths (the top-level school dashboard and class-list summaries) are
/// served through <see cref="ISchoolDashboardQueryCache"/> with a short TTL;
/// the Phase 4 event consumer invalidates the cache on write. Every query is
/// tenant + school-tenant scoped — cross-school data is unreachable.
///
/// <see cref="RebuildForSchoolAsync"/> is invoked by the Phase 4 event
/// consumer (T086). It reads the current Phase 4 state for students enrolled
/// in the school, derives the per-class and per-(grade,subject) aggregates,
/// and upserts them with the originating event id for idempotency.
/// </summary>
public sealed record SchoolMasteryRow(int Grade, string SubjectId, decimal AverageMastery, string MasteryBand);

public sealed record SchoolAtRiskPerClass(Guid ClassGroupId, string DisplayNameAr, string DisplayNameEn, int AtRiskCount);

public sealed record SchoolEngagement(decimal AverageSessionFrequency, int ActiveStreakCount, int BadgesAwardedCount);

public sealed record SchoolAtRiskSummary(int TotalAtRisk, IReadOnlyList<SchoolAtRiskPerClass> PerClass);

public sealed record SchoolDashboardResponse(
    Guid SchoolTenantId,
    string SchoolNameAr,
    string SchoolNameEn,
    int TotalStudents,
    int ActiveStudents,
    IReadOnlyList<SchoolMasteryRow> SchoolMastery,
    SchoolEngagement Engagement,
    SchoolAtRiskSummary AtRisk,
    int RecentReportsCount,
    DateTime LastUpdatedAt);

public sealed record ClassStudentMasteryRow(string SubjectId, decimal MasteryScore, string MasteryBand);

public sealed record ClassStudentRow(
    Guid StudentId,
    string DisplayNameAr,
    string DisplayNameEn,
    IReadOnlyList<ClassStudentMasteryRow> MasterySummary,
    int StreakLength,
    int BadgesEarned,
    bool AtRisk,
    int FocusAreasCount,
    DateTime? LastActivityAt);

public sealed record ClassSubjectMasteryRow(string SubjectId, decimal AverageMastery);

public sealed record ClassDetailResponse(
    Guid ClassGroupId,
    string DisplayNameAr,
    string DisplayNameEn,
    int StudentCount,
    IReadOnlyList<ClassStudentRow> Students,
    IReadOnlyList<ClassSubjectMasteryRow> ClassMastery);

public interface ISchoolDashboardService
{
    Task<SchoolDashboardResponse?> GetSchoolDashboardAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default);

    Task<ClassDetailResponse?> GetClassDetailAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the SchoolAggregateView rows for the school from current Phase 4
    /// state. Idempotent via <paramref name="eventId"/>. Invoked by the Phase 4
    /// downstream event consumer (T086).
    /// </summary>
    Task RebuildForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid eventId,
        CancellationToken ct = default);
}

public sealed class SchoolDashboardService : ISchoolDashboardService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly MuallimiDbContext _db;
    private readonly IClassGroupRepository _classes;
    private readonly IClassEnrolmentRepository _enrolments;
    private readonly ISchoolAggregateViewRepository _aggregates;
    private readonly ISchoolDashboardQueryCache _cache;

    public SchoolDashboardService(
        MuallimiDbContext db,
        IClassGroupRepository classes,
        IClassEnrolmentRepository enrolments,
        ISchoolAggregateViewRepository aggregates,
        ISchoolDashboardQueryCache cache)
    {
        _db = db;
        _classes = classes;
        _enrolments = enrolments;
        _aggregates = aggregates;
        _cache = cache;
    }

    public Task<SchoolDashboardResponse?> GetSchoolDashboardAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default)
    {
        var key = $"school:{schoolTenantId:N}:overview";
        return _cache.GetOrSetAsync<SchoolDashboardResponse?>(
            key,
            CacheTtl,
            async token => await BuildSchoolDashboardAsync(tenantId, schoolTenantId, token),
            ct);
    }

    public Task<ClassDetailResponse?> GetClassDetailAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        CancellationToken ct = default)
    {
        var key = $"school:{schoolTenantId:N}:class:{classGroupId:N}";
        return _cache.GetOrSetAsync<ClassDetailResponse?>(
            key,
            CacheTtl,
            async token => await BuildClassDetailAsync(tenantId, schoolTenantId, classGroupId, token),
            ct);
    }

    public async Task RebuildForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid eventId,
        CancellationToken ct = default)
    {
        var classes = await _classes.ListForSchoolAsync(tenantId, schoolTenantId, ct);
        if (classes.Count == 0)
        {
            await _aggregates.SaveChangesAsync(ct);
            _cache.Invalidate($"school:{schoolTenantId:N}");
            return;
        }

        var classIds = classes.Select(c => c.ClassGroupId).ToHashSet();
        var enrolmentsByClass = new Dictionary<Guid, List<Guid>>();
        var allStudentIds = new HashSet<Guid>();
        foreach (var c in classes)
        {
            var rows = await _enrolments.ListActiveForClassAsync(tenantId, c.ClassGroupId, ct);
            var ids = rows.Select(r => r.StudentId).ToList();
            enrolmentsByClass[c.ClassGroupId] = ids;
            foreach (var id in ids) allStudentIds.Add(id);
        }

        var mastery = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && allStudentIds.Contains(m.StudentId))
            .ToListAsync(ct);
        var streaks = await _db.StreakStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && allStudentIds.Contains(s.StudentId))
            .ToListAsync(ct);
        var badges = await _db.BadgeAwards
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && allStudentIds.Contains(b.StudentId))
            .ToListAsync(ct);
        var atRiskOpen = await _db.AtRiskFlags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && allStudentIds.Contains(f.StudentId) && f.ClearedAt == null)
            .ToListAsync(ct);

        // Per-class aggregate rows.
        foreach (var c in classes)
        {
            var classStudents = enrolmentsByClass[c.ClassGroupId];
            var classStudentSet = classStudents.ToHashSet();

            var classMastery = mastery.Where(m => classStudentSet.Contains(m.StudentId)).ToList();
            var classAvg = classMastery.Count == 0 ? 0m : Math.Round(classMastery.Average(m => m.MasteryScore), 4);
            var classAtRisk = atRiskOpen.Count(f => classStudentSet.Contains(f.StudentId));
            var classStreaks = streaks.Count(s => classStudentSet.Contains(s.StudentId) && s.CurrentLength > 0);
            var classBadges = badges.Count(b => classStudentSet.Contains(b.StudentId));

            await _aggregates.UpsertAsync(new SchoolAggregateView
            {
                TenantId = tenantId,
                SchoolTenantId = schoolTenantId,
                ScopeType = "class",
                ScopeId = c.ClassGroupId,
                Grade = c.Grade,
                SubjectId = null,
                ActiveStudentCount = classStudents.Count,
                AverageMastery = classAvg,
                AtRiskCount = classAtRisk,
                ActiveStreakCount = classStreaks,
                BadgesAwardedCount = classBadges,
            }, eventId, ct);
        }

        // Per-(grade, subject) aggregate rows for the school-wide mastery tiles.
        var classesByGrade = classes.GroupBy(c => c.Grade);
        foreach (var gradeGroup in classesByGrade)
        {
            var studentsInGrade = gradeGroup
                .SelectMany(c => enrolmentsByClass.TryGetValue(c.ClassGroupId, out var ids) ? ids : new List<Guid>())
                .ToHashSet();

            var masteryByGradeSubject = mastery
                .Where(m => studentsInGrade.Contains(m.StudentId))
                .GroupBy(m => m.SubjectId);

            foreach (var subjectGroup in masteryByGradeSubject)
            {
                var scores = subjectGroup.Select(x => x.MasteryScore).ToList();
                if (scores.Count == 0) continue;
                var avg = Math.Round(scores.Average(), 4);

                await _aggregates.UpsertAsync(new SchoolAggregateView
                {
                    TenantId = tenantId,
                    SchoolTenantId = schoolTenantId,
                    ScopeType = "school",
                    ScopeId = schoolTenantId,
                    Grade = gradeGroup.Key,
                    SubjectId = subjectGroup.Key,
                    ActiveStudentCount = studentsInGrade.Count,
                    AverageMastery = avg,
                    AtRiskCount = 0,
                    ActiveStreakCount = 0,
                    BadgesAwardedCount = 0,
                }, eventId, ct);
            }
        }

        await _aggregates.SaveChangesAsync(ct);
        _cache.Invalidate($"school:{schoolTenantId:N}");
    }

    private async Task<SchoolDashboardResponse?> BuildSchoolDashboardAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct)
    {
        var school = await _db.SchoolTenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SchoolTenantId == schoolTenantId, ct);
        if (school is null) return null;

        var classes = await _classes.ListForSchoolAsync(tenantId, schoolTenantId, ct);
        var classMap = classes.ToDictionary(c => c.ClassGroupId, c => c);

        var aggregates = await _aggregates.ListForSchoolAsync(tenantId, schoolTenantId, ct);

        var schoolMasteryRows = aggregates
            .Where(a => a.ScopeType == "school" && a.SubjectId != null && a.Grade != null)
            .OrderBy(a => a.Grade)
            .ThenBy(a => a.SubjectId)
            .Select(a => new SchoolMasteryRow(
                Grade: a.Grade ?? 0,
                SubjectId: a.SubjectId?.ToString() ?? string.Empty,
                AverageMastery: a.AverageMastery,
                MasteryBand: ClassifyBand(a.AverageMastery)))
            .ToList();

        var classAggregates = aggregates.Where(a => a.ScopeType == "class").ToList();
        var totalStudents = classAggregates.Sum(a => a.ActiveStudentCount);
        var activeStreaks = classAggregates.Sum(a => a.ActiveStreakCount);
        var badgesAwarded = classAggregates.Sum(a => a.BadgesAwardedCount);
        var totalAtRisk = classAggregates.Sum(a => a.AtRiskCount);

        var perClass = classAggregates
            .Where(a => a.AtRiskCount > 0 && classMap.ContainsKey(a.ScopeId))
            .OrderByDescending(a => a.AtRiskCount)
            .Select(a => new SchoolAtRiskPerClass(
                ClassGroupId: a.ScopeId,
                DisplayNameAr: classMap[a.ScopeId].DisplayNameAr,
                DisplayNameEn: classMap[a.ScopeId].DisplayNameEn,
                AtRiskCount: a.AtRiskCount))
            .ToList();

        var avgSessionFrequency = totalStudents == 0
            ? 0m
            : Math.Round((decimal)activeStreaks / Math.Max(1, totalStudents), 4);

        var recentReportsCount = await _db.SchoolReports
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.SchoolTenantId == schoolTenantId)
            .CountAsync(ct);

        var lastUpdatedAt = aggregates.Count == 0
            ? school.UpdatedAt
            : aggregates.Max(a => a.LastUpdatedAt);

        return new SchoolDashboardResponse(
            SchoolTenantId: schoolTenantId,
            SchoolNameAr: school.SchoolNameAr,
            SchoolNameEn: school.SchoolNameEn,
            TotalStudents: totalStudents,
            ActiveStudents: totalStudents,
            SchoolMastery: schoolMasteryRows,
            Engagement: new SchoolEngagement(avgSessionFrequency, activeStreaks, badgesAwarded),
            AtRisk: new SchoolAtRiskSummary(totalAtRisk, perClass),
            RecentReportsCount: recentReportsCount,
            LastUpdatedAt: lastUpdatedAt);
    }

    private async Task<ClassDetailResponse?> BuildClassDetailAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid classGroupId,
        CancellationToken ct)
    {
        var classGroup = await _classes.GetByIdAsync(tenantId, schoolTenantId, classGroupId, ct);
        if (classGroup is null) return null;

        var enrolled = await _enrolments.ListActiveForClassAsync(tenantId, classGroupId, ct);
        var studentIds = enrolled.Select(e => e.StudentId).ToHashSet();

        var students = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && studentIds.Contains(s.Id))
            .Select(s => new { s.Id, s.DisplayName })
            .ToListAsync(ct);

        var mastery = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && studentIds.Contains(m.StudentId))
            .ToListAsync(ct);
        var streaks = await _db.StreakStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && studentIds.Contains(s.StudentId))
            .ToListAsync(ct);
        var badgeCounts = await _db.BadgeAwards
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && studentIds.Contains(b.StudentId))
            .GroupBy(b => b.StudentId)
            .Select(g => new { StudentId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var focusCounts = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && studentIds.Contains(f.StudentId))
            .GroupBy(f => f.StudentId)
            .Select(g => new { StudentId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var atRiskOpen = await _db.AtRiskFlags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && studentIds.Contains(f.StudentId) && f.ClearedAt == null)
            .Select(f => f.StudentId)
            .ToListAsync(ct);
        var atRiskSet = atRiskOpen.ToHashSet();

        var masteryByStudent = mastery.GroupBy(m => m.StudentId).ToDictionary(g => g.Key, g => g.ToList());
        var streakByStudent = streaks.ToDictionary(s => s.StudentId, s => s);
        var badgeByStudent = badgeCounts.ToDictionary(b => b.StudentId, b => b.Count);
        var focusByStudent = focusCounts.ToDictionary(f => f.StudentId, f => f.Count);

        var studentRows = students.Select(s =>
        {
            var mList = masteryByStudent.TryGetValue(s.Id, out var list) ? list : new List<MasteryState>();
            var masterySummary = mList
                .GroupBy(m => m.SubjectId)
                .Select(g =>
                {
                    var avg = Math.Round(g.Average(m => m.MasteryScore), 4);
                    return new ClassStudentMasteryRow(
                        SubjectId: g.Key.ToString(),
                        MasteryScore: avg,
                        MasteryBand: ClassifyBand(avg));
                })
                .ToList();
            var lastActivity = mList.Count == 0 ? (DateTime?)null : mList.Max(m => m.LastUpdatedAt);
            var streakLen = streakByStudent.TryGetValue(s.Id, out var st) ? st.CurrentLength : 0;
            return new ClassStudentRow(
                StudentId: s.Id,
                DisplayNameAr: s.DisplayName,
                DisplayNameEn: s.DisplayName,
                MasterySummary: masterySummary,
                StreakLength: streakLen,
                BadgesEarned: badgeByStudent.TryGetValue(s.Id, out var bc) ? bc : 0,
                AtRisk: atRiskSet.Contains(s.Id),
                FocusAreasCount: focusByStudent.TryGetValue(s.Id, out var fc) ? fc : 0,
                LastActivityAt: lastActivity);
        }).ToList();

        var classMastery = mastery
            .GroupBy(m => m.SubjectId)
            .Select(g => new ClassSubjectMasteryRow(
                SubjectId: g.Key.ToString(),
                AverageMastery: Math.Round(g.Average(m => m.MasteryScore), 4)))
            .ToList();

        return new ClassDetailResponse(
            ClassGroupId: classGroupId,
            DisplayNameAr: classGroup.DisplayNameAr,
            DisplayNameEn: classGroup.DisplayNameEn,
            StudentCount: studentRows.Count,
            Students: studentRows,
            ClassMastery: classMastery);
    }

    public static string ClassifyBand(decimal score)
        => score switch
        {
            < 0.25m => "introduced",
            < 0.50m => "practicing",
            < 0.75m => "on_track",
            _ => "confident",
        };
}

public static class SchoolDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolDashboardService(this IServiceCollection services)
    {
        services.AddScoped<ISchoolDashboardService, SchoolDashboardService>();
        return services;
    }
}
