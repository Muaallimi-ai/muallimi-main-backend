using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.StudentProgressSurface;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.ParentDashboard;

/// <summary>
/// T071 (US2) — Parent dashboard aggregation service.
///
/// Joins mastery, focus areas, recent activity (ProgressRecord tail),
/// latest weekly report reference, at-risk flag, and a read-only plan
/// view into the payload shape pinned by
/// <c>specs/006-engagement-progress-parent/contracts/parent-dashboard-contract.md</c>.
///
/// Every query filters on <c>(tenant_id, student_id)</c>. The caller MUST
/// have already resolved an active <see cref="Muallimi.Domain.Parents.ChildLink"/>
/// via <see cref="IChildLinkRepository.GetActiveAsync"/> — the service does
/// not re-validate ownership; callers that skip that step would leak
/// cross-family rows.
/// </summary>
public interface IParentDashboardService
{
    Task<ParentChildListItem[]> ListChildrenAsync(
        Guid tenantId,
        Guid parentProfileId,
        CancellationToken ct = default);

    Task<ParentDashboardPayload> BuildDashboardAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid childId,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record ParentChildListItem(
    Guid ChildId,
    string DisplayName,
    string CurriculumType,
    string Grade,
    string PreferredLanguage);

public sealed record ParentDashboardPayload(
    Guid ChildId,
    string CurriculumType,
    string Grade,
    IReadOnlyList<ParentMasterySubject> MasteryBySubject,
    IReadOnlyList<ParentFocusArea> FocusAreasThisWeek,
    IReadOnlyList<ParentRecentActivity> RecentActivity,
    ParentLatestWeeklyReport? LatestWeeklyReport,
    ParentPlanView PlanView,
    ParentAtRiskFlag? AtRiskFlag,
    ParentStreakSummary Streak,
    IReadOnlyList<ParentBadgeSummary> Badges,
    string CorrelationId);

// T122 (US6): streak + badge facets surfaced through the parent dashboard.
public sealed record ParentStreakSummary(
    int CurrentLength,
    int LongestLength,
    string? LastQualifyingDay,
    string FamilyTimezone);

public sealed record ParentBadgeSummary(
    Guid BadgeAwardId,
    string BadgeKey,
    string BadgeCriterionVersion,
    DateTime AwardedAt,
    string DisplayNameAr,
    string DisplayNameEn);

public sealed record ParentMasterySubject(
    Guid SubjectId,
    string SubjectLabelAr,
    string SubjectLabelEn,
    decimal MasteryScore,
    string MasteryBand,
    decimal DeltaSinceLastWeek);

public sealed record ParentFocusArea(
    Guid FocusAreaId,
    Guid SubjectId,
    Guid TopicId,
    string RationaleAr,
    string RationaleEn,
    ParentFocusNextStep SuggestedNextStep);

public sealed record ParentFocusNextStep(string Phase3Mode, string DeepLink);

public sealed record ParentRecentActivity(
    DateTime OccurredAt,
    string SummaryAr,
    string SummaryEn,
    IReadOnlyDictionary<string, string> CurriculumScope);

public sealed record ParentLatestWeeklyReport(
    Guid WeeklyReportId,
    DateTime WindowStart,
    DateTime WindowEnd,
    string SummaryAr,
    string SummaryEn,
    string Status);

public sealed record ParentPlanView(
    string PlanTier,
    IReadOnlyList<string> Entitlements,
    bool IsReadOnly);

public sealed record ParentAtRiskFlag(
    DateTime RaisedAt,
    Guid? LinkedInterventionPromptId,
    string Status);

public sealed class ParentDashboardService : IParentDashboardService
{
    private readonly MuallimiDbContext _db;
    private readonly ICurriculumLabelResolver _labels;

    public ParentDashboardService(MuallimiDbContext db, ICurriculumLabelResolver labels)
    {
        _db = db;
        _labels = labels;
    }

    public async Task<ParentChildListItem[]> ListChildrenAsync(
        Guid tenantId,
        Guid parentProfileId,
        CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var links = await _db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.ParentProfileId == parentProfileId)
            .Where(l => l.EffectiveStart <= today && (l.EffectiveEnd == null || l.EffectiveEnd >= today))
            .ToListAsync(ct);

        if (links.Count == 0) return Array.Empty<ParentChildListItem>();

        var childIds = links.Select(l => l.StudentId).Distinct().ToArray();
        var profiles = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && childIds.Contains(s.Id))
            .ToListAsync(ct);

        var lookup = profiles.ToDictionary(p => p.Id);

        return links
            .OrderBy(l => l.EffectiveStart)
            .Select(l =>
            {
                lookup.TryGetValue(l.StudentId, out var profile);
                return new ParentChildListItem(
                    ChildId: l.StudentId,
                    DisplayName: profile?.DisplayName ?? l.StudentId.ToString(),
                    CurriculumType: profile?.CurriculumType ?? "moe",
                    Grade: profile?.Grade ?? string.Empty,
                    PreferredLanguage: profile?.PreferredLanguage ?? "ar");
            })
            .ToArray();
    }

    public async Task<ParentDashboardPayload> BuildDashboardAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid childId,
        string correlationId,
        CancellationToken ct = default)
    {
        var profile = await _db.StudentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == childId, ct);

        var masteryRows = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.StudentId == childId
                        && m.CalculationVersion == MasteryCalculator.Version)
            .ToListAsync(ct);
        var mastery = BuildMasteryBySubject(masteryRows);

        var focusRows = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId
                        && f.StudentId == childId
                        && f.ValidUntil > DateTime.UtcNow)
            .OrderByDescending(f => f.ComputedAt)
            .Take(5)
            .ToListAsync(ct);
        var focus = focusRows.Select(BuildFocus).ToList();

        var recentRows = await _db.ProgressRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.StudentId == childId)
            .OrderByDescending(p => p.OccurredAt)
            .Take(10)
            .ToListAsync(ct);
        var recent = recentRows.Select(BuildRecent).ToList();

        var latestReport = await _db.WeeklyReports
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.StudentId == childId)
            .OrderByDescending(r => r.WindowEnd)
            .FirstOrDefaultAsync(ct);
        var latest = latestReport is null
            ? null
            : new ParentLatestWeeklyReport(
                WeeklyReportId: latestReport.WeeklyReportId,
                WindowStart: latestReport.WindowStart,
                WindowEnd: latestReport.WindowEnd,
                SummaryAr: latestReport.SummaryAr,
                SummaryEn: latestReport.SummaryEn,
                Status: latestReport.Status);

        var streakRow = await _db.StreakStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.StudentId == childId, ct);
        var streak = streakRow is null
            ? new ParentStreakSummary(0, 0, null, "Asia/Dubai")
            : new ParentStreakSummary(
                streakRow.CurrentLength,
                streakRow.LongestLength,
                streakRow.LastQualifyingDay == DateTime.MinValue
                    ? null
                    : streakRow.LastQualifyingDay.ToString("yyyy-MM-dd"),
                streakRow.FamilyTimezone);

        var badgeAwardRows = await _db.BadgeAwards
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.StudentId == childId)
            .OrderByDescending(b => b.AwardedAt)
            .Take(10)
            .ToListAsync(ct);
        IReadOnlyList<ParentBadgeSummary> badges;
        if (badgeAwardRows.Count == 0)
        {
            badges = Array.Empty<ParentBadgeSummary>();
        }
        else
        {
            var criteria = await _db.BadgeCriteria
                .AsNoTracking()
                .ToListAsync(ct);
            var criterionLookup = criteria.ToDictionary(c => (c.BadgeCriterionId, c.Version));
            badges = badgeAwardRows.Select(a =>
            {
                criterionLookup.TryGetValue((a.BadgeCriterionId, a.BadgeCriterionVersion), out var criterion);
                return new ParentBadgeSummary(
                    BadgeAwardId: a.BadgeAwardId,
                    BadgeKey: criterion?.BadgeKey ?? a.BadgeCriterionId.ToString(),
                    BadgeCriterionVersion: a.BadgeCriterionVersion,
                    AwardedAt: a.AwardedAt,
                    DisplayNameAr: criterion?.DisplayNameAr ?? a.BadgeCriterionId.ToString(),
                    DisplayNameEn: criterion?.DisplayNameEn ?? a.BadgeCriterionId.ToString());
            }).ToList();
        }

        var atRisk = await _db.AtRiskFlags
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.StudentId == childId && f.ClearedAt == null)
            .OrderByDescending(f => f.RaisedAt)
            .FirstOrDefaultAsync(ct);
        var atRiskFlag = atRisk is null
            ? null
            : new ParentAtRiskFlag(
                RaisedAt: atRisk.RaisedAt,
                LinkedInterventionPromptId: atRisk.LinkedInterventionPromptId,
                Status: atRisk.ClearedAt.HasValue ? "cleared" : "active");

        var planTier = profile?.PlanTier ?? "free";
        var planView = new ParentPlanView(
            PlanTier: planTier,
            Entitlements: EntitlementsFor(planTier),
            IsReadOnly: true);

        var curriculumType = profile?.CurriculumType
                             ?? masteryRows.FirstOrDefault()?.CurriculumType
                             ?? focusRows.FirstOrDefault()?.CurriculumType
                             ?? "moe";
        var grade = profile?.Grade ?? string.Empty;

        return new ParentDashboardPayload(
            ChildId: childId,
            CurriculumType: curriculumType,
            Grade: grade,
            MasteryBySubject: mastery,
            FocusAreasThisWeek: focus,
            RecentActivity: recent,
            LatestWeeklyReport: latest,
            PlanView: planView,
            AtRiskFlag: atRiskFlag,
            Streak: streak,
            Badges: badges,
            CorrelationId: correlationId);
    }

    private IReadOnlyList<ParentMasterySubject> BuildMasteryBySubject(IReadOnlyList<MasteryState> rows)
    {
        var bySubject = rows
            .GroupBy(r => r.SubjectId)
            .OrderBy(g => g.Key);

        var result = new List<ParentMasterySubject>();
        foreach (var group in bySubject)
        {
            var rollup = group.FirstOrDefault(r => r.TopicId == null);
            var score = rollup?.MasteryScore
                        ?? (group.Any()
                            ? group.Where(r => r.TopicId.HasValue).Average(r => r.MasteryScore)
                            : 0m);
            var band = rollup?.MasteryBand ?? MasteryCalculator.BandFor(score);

            var (ar, en) = _labels.ResolveSubject(group.Key);
            result.Add(new ParentMasterySubject(
                SubjectId: group.Key,
                SubjectLabelAr: ar,
                SubjectLabelEn: en,
                MasteryScore: score,
                MasteryBand: band,
                DeltaSinceLastWeek: 0m));
        }
        return result;
    }

    private static ParentFocusArea BuildFocus(FocusArea row)
    {
        var (mode, link) = ParseNextStep(row.SuggestedNextStep);
        return new ParentFocusArea(
            FocusAreaId: row.FocusAreaId,
            SubjectId: row.SubjectId,
            TopicId: row.TopicId,
            RationaleAr: row.RationaleAr,
            RationaleEn: row.RationaleEn,
            SuggestedNextStep: new ParentFocusNextStep(mode, link));
    }

    private static ParentRecentActivity BuildRecent(ProgressRecord row)
    {
        var scope = ParseScope(row.CurriculumScope);
        var (ar, en) = SummariseEventKind(row.EventKind);
        return new ParentRecentActivity(
            OccurredAt: row.OccurredAt,
            SummaryAr: ar,
            SummaryEn: en,
            CurriculumScope: scope);
    }

    private static (string Ar, string En) SummariseEventKind(string eventKind) => eventKind switch
    {
        "session_start" => ("بدأ جلسة دراسة", "Started a study session"),
        "lesson_view" => ("استعرض درسًا", "Viewed a lesson"),
        "content_play" => ("شغّل محتوى", "Played learning content"),
        "question_asked" => ("طرح سؤالًا على المعلم الذكي", "Asked the AI tutor a question"),
        "answer_received" => ("تلقّى إجابة من المعلم", "Received a tutor answer"),
        "refusal" => ("رفض المعلم الإجابة خارج المنهج", "Tutor refused an out-of-scope question"),
        "quiz_answered" => ("أجاب على سؤال تدريبي", "Answered a practice question"),
        "mock_test" => ("أنهى اختبارًا محاكيًا", "Completed a mock test"),
        "homework_help_used" => ("استخدم مساعدة الواجب", "Used homework help"),
        "whiteboard_session" => ("عمل على السبورة التفاعلية", "Worked on the interactive whiteboard"),
        "session_end" => ("أنهى جلسته", "Ended the session"),
        _ => ("تقدم جديد", "Progress update"),
    };

    private static IReadOnlyDictionary<string, string> ParseScope(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => prop.Value.GetRawText(),
                };
            }
        }
        catch
        {
            // Fall through to empty dictionary.
        }
        return result;
    }

    private static (string Mode, string DeepLink) ParseNextStep(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return ("study", "/study");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ("study", "/study");
            var mode = doc.RootElement.TryGetProperty("phase3_mode", out var m)
                ? m.GetString() ?? "study" : "study";
            var deepLink = doc.RootElement.TryGetProperty("deep_link", out var l)
                ? l.GetString() ?? "/study" : "/study";
            return (mode, deepLink);
        }
        catch
        {
            return ("study", "/study");
        }
    }

    private static IReadOnlyList<string> EntitlementsFor(string planTier) => planTier switch
    {
        "family_plus" => new[] { "ai_tutor", "weekly_report", "whiteboard", "mock_test" },
        "family" => new[] { "ai_tutor", "weekly_report", "mock_test" },
        _ => new[] { "ai_tutor", "weekly_report" },
    };
}

public static class ParentDashboardServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ParentDashboardService(this IServiceCollection services)
    {
        services.AddScoped<IParentDashboardService, ParentDashboardService>();
        return services;
    }
}
