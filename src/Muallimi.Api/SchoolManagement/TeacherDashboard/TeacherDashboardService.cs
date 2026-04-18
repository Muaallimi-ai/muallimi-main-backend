using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.ClassManagement;
using Muallimi.Api.SchoolManagement.TeacherAssignment;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.TeacherDashboard;

/// <summary>
/// T106 (US5) — <c>TeacherDashboardService</c>.
///
/// Scoped strictly to the teacher's active <see cref="TeacherAssignment"/>
/// rows (class + subject). Three responses are modelled:
///
///   • <see cref="GetTeacherDashboardAsync"/> — list of assigned (class,
///     subject) pairs with aggregate mastery and at-risk counts;
///   • <see cref="GetClassSubjectDetailAsync"/> — per-student mastery /
///     focus-area / at-risk detail for a specific (class, subject);
///   • <see cref="GetStudentDetailAsync"/> — student view restricted to the
///     subjects the teacher is assigned to teach the student.
///
/// Billing, plan tier, and family-private data are NEVER surfaced: the
/// service projects only the fields enumerated below and the endpoints
/// project only what this service returns. See
/// <see cref="TeacherPrivacyTests"/> in the contract suite.
/// </summary>
public sealed record TeacherAssignedClassRow(
    Guid ClassGroupId,
    string ClassDisplayNameAr,
    string ClassDisplayNameEn,
    Guid SubjectId,
    string SubjectNameAr,
    string SubjectNameEn,
    int StudentCount,
    decimal AverageMastery,
    int AtRiskCount);

public sealed record TeacherDashboardResponse(
    Guid TeacherId,
    IReadOnlyList<TeacherAssignedClassRow> AssignedClasses);

public sealed record TeacherFocusAreaRow(
    string TopicNameAr,
    string TopicNameEn,
    string RationaleAr,
    string RationaleEn);

public sealed record TeacherClassSubjectStudentRow(
    Guid StudentId,
    string DisplayNameAr,
    string DisplayNameEn,
    decimal MasteryScore,
    string MasteryBand,
    IReadOnlyList<TeacherFocusAreaRow> FocusAreas,
    bool AtRisk,
    int StreakLength,
    DateTime? LastActivityAt);

public sealed record TeacherClassSubjectDetailResponse(
    Guid ClassGroupId,
    Guid SubjectId,
    IReadOnlyList<TeacherClassSubjectStudentRow> Students);

public sealed record TeacherStudentTopicMastery(
    Guid TopicId,
    string TopicNameAr,
    string TopicNameEn,
    decimal MasteryScore);

public sealed record TeacherStudentSubjectMastery(
    Guid SubjectId,
    string SubjectNameAr,
    string SubjectNameEn,
    decimal MasteryScore,
    string MasteryBand,
    IReadOnlyList<TeacherStudentTopicMastery> Topics);

public sealed record TeacherStudentFocusAreaRow(
    string TopicNameAr,
    string TopicNameEn,
    string RationaleAr,
    string RationaleEn,
    string DeepLink);

public sealed record TeacherStudentBadgeRow(
    string BadgeKey,
    string BadgeNameAr,
    string BadgeNameEn,
    DateTime AwardedAt);

public sealed record TeacherStudentInterventionPrompt(
    string BodyAr,
    string BodyEn,
    string NextStepPhase3Mode,
    string NextStepDeepLink);

public sealed record TeacherStudentDetailResponse(
    Guid StudentId,
    string DisplayNameAr,
    string DisplayNameEn,
    IReadOnlyList<TeacherStudentSubjectMastery> Mastery,
    IReadOnlyList<TeacherStudentFocusAreaRow> FocusAreas,
    int StreakLength,
    IReadOnlyList<TeacherStudentBadgeRow> Badges,
    bool AtRisk,
    TeacherStudentInterventionPrompt? InterventionPrompt);

public interface ITeacherDashboardService
{
    Task<TeacherDashboardResponse?> GetTeacherDashboardAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid teacherId,
        CancellationToken ct = default);

    Task<TeacherClassSubjectDetailResponse?> GetClassSubjectDetailAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid teacherId,
        Guid classGroupId,
        Guid subjectId,
        CancellationToken ct = default);

    Task<TeacherStudentDetailResponse?> GetStudentDetailAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid teacherId,
        Guid studentId,
        CancellationToken ct = default);
}

public sealed class TeacherDashboardService : ITeacherDashboardService
{
    private readonly MuallimiDbContext _db;
    private readonly IClassGroupRepository _classes;
    private readonly IClassEnrolmentRepository _enrolments;
    private readonly ITeacherAssignmentRepository _assignments;
    private readonly ITeacherRepository _teachers;

    public TeacherDashboardService(
        MuallimiDbContext db,
        IClassGroupRepository classes,
        IClassEnrolmentRepository enrolments,
        ITeacherAssignmentRepository assignments,
        ITeacherRepository teachers)
    {
        _db = db;
        _classes = classes;
        _enrolments = enrolments;
        _assignments = assignments;
        _teachers = teachers;
    }

    public async Task<TeacherDashboardResponse?> GetTeacherDashboardAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid teacherId,
        CancellationToken ct = default)
    {
        var teacher = await _teachers.GetByIdAsync(tenantId, schoolTenantId, teacherId, ct);
        if (teacher is null || teacher.DeactivatedAt != null) return null;

        var assignments = await _assignments.ListActiveForTeacherAsync(tenantId, teacherId, ct);
        if (assignments.Count == 0)
        {
            return new TeacherDashboardResponse(teacherId, Array.Empty<TeacherAssignedClassRow>());
        }

        var classIds = assignments.Select(a => a.ClassGroupId).Distinct().ToList();
        var classes = await _db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId
                && c.SchoolTenantId == schoolTenantId
                && classIds.Contains(c.ClassGroupId))
            .ToListAsync(ct);
        var classMap = classes.ToDictionary(c => c.ClassGroupId, c => c);

        var rows = new List<TeacherAssignedClassRow>(assignments.Count);
        foreach (var a in assignments.OrderBy(x => x.ClassGroupId).ThenBy(x => x.SubjectId))
        {
            if (!classMap.TryGetValue(a.ClassGroupId, out var classGroup)) continue;

            var enrolled = await _enrolments.ListActiveForClassAsync(tenantId, a.ClassGroupId, ct);
            var studentIds = enrolled.Select(e => e.StudentId).ToHashSet();

            var mastery = await _db.MasteryStates
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId
                    && m.SubjectId == a.SubjectId
                    && studentIds.Contains(m.StudentId))
                .ToListAsync(ct);

            var avg = mastery.Count == 0
                ? 0m
                : Math.Round(mastery.GroupBy(m => m.StudentId).Average(g => g.Average(m => m.MasteryScore)), 4);

            var atRisk = await _db.AtRiskFlags
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(f =>
                    f.TenantId == tenantId
                    && studentIds.Contains(f.StudentId)
                    && f.ClearedAt == null, ct);

            rows.Add(new TeacherAssignedClassRow(
                ClassGroupId: classGroup.ClassGroupId,
                ClassDisplayNameAr: classGroup.DisplayNameAr,
                ClassDisplayNameEn: classGroup.DisplayNameEn,
                SubjectId: a.SubjectId,
                SubjectNameAr: SubjectIdCatalogue.ArabicName(a.SubjectId),
                SubjectNameEn: SubjectIdCatalogue.EnglishName(a.SubjectId),
                StudentCount: studentIds.Count,
                AverageMastery: avg,
                AtRiskCount: atRisk));
        }

        return new TeacherDashboardResponse(teacherId, rows);
    }

    public async Task<TeacherClassSubjectDetailResponse?> GetClassSubjectDetailAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid teacherId,
        Guid classGroupId,
        Guid subjectId,
        CancellationToken ct = default)
    {
        var assignment = await _assignments.GetActiveAsync(tenantId, teacherId, classGroupId, subjectId, ct);
        if (assignment is null) return null;

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
            .Where(m => m.TenantId == tenantId
                && m.SubjectId == subjectId
                && studentIds.Contains(m.StudentId))
            .ToListAsync(ct);

        var focusAreas = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId
                && f.SubjectId == subjectId
                && studentIds.Contains(f.StudentId)
                && f.ValidUntil > DateTime.UtcNow.AddDays(-30))
            .ToListAsync(ct);

        var streaks = await _db.StreakStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && studentIds.Contains(s.StudentId))
            .ToListAsync(ct);

        var openFlags = await _db.AtRiskFlags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && studentIds.Contains(f.StudentId) && f.ClearedAt == null)
            .Select(f => f.StudentId)
            .ToListAsync(ct);
        var atRiskSet = openFlags.ToHashSet();

        var masteryByStudent = mastery
            .GroupBy(m => m.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var focusByStudent = focusAreas
            .GroupBy(f => f.StudentId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var streakByStudent = streaks.ToDictionary(s => s.StudentId, s => s);

        var rows = students
            .OrderBy(s => s.DisplayName)
            .Select(s =>
            {
                var mList = masteryByStudent.TryGetValue(s.Id, out var ml) ? ml : new List<MasteryState>();
                var avg = mList.Count == 0 ? 0m : Math.Round(mList.Average(m => m.MasteryScore), 4);
                var band = ClassifyBand(avg);
                var focusList = focusByStudent.TryGetValue(s.Id, out var fl)
                    ? fl.Select(f => new TeacherFocusAreaRow(
                        TopicNameAr: TopicIdCatalogue.ArabicName(f.TopicId),
                        TopicNameEn: TopicIdCatalogue.EnglishName(f.TopicId),
                        RationaleAr: f.RationaleAr,
                        RationaleEn: f.RationaleEn)).ToList()
                    : new List<TeacherFocusAreaRow>();
                var lastActivity = mList.Count == 0 ? (DateTime?)null : mList.Max(m => m.LastUpdatedAt);
                var streakLen = streakByStudent.TryGetValue(s.Id, out var ss) ? ss.CurrentLength : 0;
                return new TeacherClassSubjectStudentRow(
                    StudentId: s.Id,
                    DisplayNameAr: s.DisplayName,
                    DisplayNameEn: s.DisplayName,
                    MasteryScore: avg,
                    MasteryBand: band,
                    FocusAreas: focusList,
                    AtRisk: atRiskSet.Contains(s.Id),
                    StreakLength: streakLen,
                    LastActivityAt: lastActivity);
            })
            .ToList();

        return new TeacherClassSubjectDetailResponse(classGroupId, subjectId, rows);
    }

    public async Task<TeacherStudentDetailResponse?> GetStudentDetailAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid teacherId,
        Guid studentId,
        CancellationToken ct = default)
    {
        var assignments = await _assignments.ListActiveForTeacherAsync(tenantId, teacherId, ct);
        if (assignments.Count == 0) return null;

        var classIds = assignments.Select(a => a.ClassGroupId).ToHashSet();
        var enrolment = await _db.ClassEnrolments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId
                && e.StudentId == studentId
                && e.Status == "active"
                && e.UnenrolledAt == null
                && classIds.Contains(e.ClassGroupId),
                ct);
        if (enrolment is null) return null;

        var assignedSubjects = assignments
            .Where(a => a.ClassGroupId == enrolment.ClassGroupId)
            .Select(a => a.SubjectId)
            .ToHashSet();
        if (assignedSubjects.Count == 0) return null;

        var student = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Id == studentId)
            .Select(s => new { s.Id, s.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (student is null) return null;

        var mastery = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                && m.StudentId == studentId
                && assignedSubjects.Contains(m.SubjectId))
            .ToListAsync(ct);

        var focusAreas = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId
                && f.StudentId == studentId
                && assignedSubjects.Contains(f.SubjectId))
            .ToListAsync(ct);

        var badges = await _db.BadgeAwards
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.StudentId == studentId)
            .OrderByDescending(b => b.AwardedAt)
            .ToListAsync(ct);

        var streak = await _db.StreakStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.StudentId == studentId, ct);

        var openFlag = await _db.AtRiskFlags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderByDescending(f => f.RaisedAt)
            .FirstOrDefaultAsync(f =>
                f.TenantId == tenantId
                && f.StudentId == studentId
                && f.ClearedAt == null, ct);

        var masterySubjects = mastery
            .GroupBy(m => m.SubjectId)
            .Select(g =>
            {
                var avg = Math.Round(g.Average(m => m.MasteryScore), 4);
                var topics = g
                    .Where(m => m.TopicId.HasValue)
                    .Select(m => new TeacherStudentTopicMastery(
                        TopicId: m.TopicId!.Value,
                        TopicNameAr: TopicIdCatalogue.ArabicName(m.TopicId.Value),
                        TopicNameEn: TopicIdCatalogue.EnglishName(m.TopicId.Value),
                        MasteryScore: Math.Round(m.MasteryScore, 4)))
                    .ToList();
                return new TeacherStudentSubjectMastery(
                    SubjectId: g.Key,
                    SubjectNameAr: SubjectIdCatalogue.ArabicName(g.Key),
                    SubjectNameEn: SubjectIdCatalogue.EnglishName(g.Key),
                    MasteryScore: avg,
                    MasteryBand: ClassifyBand(avg),
                    Topics: topics);
            })
            .ToList();

        var focus = focusAreas
            .Select(f => new TeacherStudentFocusAreaRow(
                TopicNameAr: TopicIdCatalogue.ArabicName(f.TopicId),
                TopicNameEn: TopicIdCatalogue.EnglishName(f.TopicId),
                RationaleAr: f.RationaleAr,
                RationaleEn: f.RationaleEn,
                DeepLink: $"/lessons/{f.TopicId:D}"))
            .ToList();

        var badgeRows = badges
            .Select(b => new TeacherStudentBadgeRow(
                BadgeKey: b.BadgeAwardId.ToString("D"),
                BadgeNameAr: string.IsNullOrWhiteSpace(b.CorrelationId) ? string.Empty : string.Empty,
                BadgeNameEn: string.Empty,
                AwardedAt: b.AwardedAt))
            .ToList();

        TeacherStudentInterventionPrompt? prompt = null;
        if (openFlag is not null)
        {
            prompt = new TeacherStudentInterventionPrompt(
                BodyAr: "يحتاج الطالب إلى دعم إضافي في المواد الموكلة إليك.",
                BodyEn: "This student needs extra support in the subjects you teach.",
                NextStepPhase3Mode: "study_mode",
                NextStepDeepLink: focus.FirstOrDefault()?.DeepLink ?? "/study-mode");
        }

        return new TeacherStudentDetailResponse(
            StudentId: studentId,
            DisplayNameAr: student.DisplayName,
            DisplayNameEn: student.DisplayName,
            Mastery: masterySubjects,
            FocusAreas: focus,
            StreakLength: streak?.CurrentLength ?? 0,
            Badges: badgeRows,
            AtRisk: openFlag is not null,
            InterventionPrompt: prompt);
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

/// <summary>
/// Minimal subject-Guid → bilingual name catalogue. Real localisation lives in
/// Phase 1 curriculum; US5 falls back to the Guid when no mapping is known.
/// </summary>
internal static class SubjectIdCatalogue
{
    private static readonly Guid MathId = Guid.Parse("00000030-0000-0000-0000-000000000001");
    private static readonly Guid ArabicId = Guid.Parse("00000030-0000-0000-0000-000000000002");
    private static readonly Guid EnglishId = Guid.Parse("00000030-0000-0000-0000-000000000003");
    private static readonly Guid ScienceId = Guid.Parse("00000030-0000-0000-0000-000000000004");

    public static string ArabicName(Guid subjectId)
    {
        if (subjectId == MathId) return "الرياضيات";
        if (subjectId == ArabicId) return "اللغة العربية";
        if (subjectId == EnglishId) return "اللغة الإنجليزية";
        if (subjectId == ScienceId) return "العلوم";
        return subjectId.ToString("D");
    }

    public static string EnglishName(Guid subjectId)
    {
        if (subjectId == MathId) return "Mathematics";
        if (subjectId == ArabicId) return "Arabic";
        if (subjectId == EnglishId) return "English";
        if (subjectId == ScienceId) return "Science";
        return subjectId.ToString("D");
    }
}

internal static class TopicIdCatalogue
{
    public static string ArabicName(Guid topicId) => topicId.ToString("D");
    public static string EnglishName(Guid topicId) => topicId.ToString("D");
}

public static class TeacherDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5TeacherDashboardService(this IServiceCollection services)
    {
        services.AddScoped<ITeacherDashboardService, TeacherDashboardService>();
        return services;
    }
}
