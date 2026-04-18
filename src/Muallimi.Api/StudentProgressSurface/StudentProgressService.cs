using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.BadgeAwarding;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentProgressSurface;

/// <summary>
/// T051 (US1) — Student progress aggregation service.
///
/// Joins the four Phase 4 state tables owned by main-backend
/// (<see cref="MasteryState"/>, <see cref="StreakState"/>,
/// <see cref="BadgeAward"/>, <see cref="FocusArea"/>) into the student-facing
/// summary shape defined by
/// <c>specs/006-engagement-progress-parent/contracts/student-progress-contract.md</c>.
///
/// The service is tenant-scoped: the caller passes the authenticated
/// <c>tenantId</c> and <c>studentId</c>; every query filters on both. No
/// row owned by any other student or tenant can be returned.
///
/// Subject/topic labels are resolved through
/// <see cref="ICurriculumLabelResolver"/> — the MVP implementation returns the
/// id as a bilingual pair so the surface renders; Phase 1 curriculum labels
/// are swapped in via DI without a service rewrite.
/// </summary>
public interface IStudentProgressService
{
    Task<StudentProgressSummary> BuildSummaryAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default);

    Task<FocusAreaDetail?> GetFocusAreaDetailAsync(
        Guid tenantId,
        Guid studentId,
        Guid focusAreaId,
        CancellationToken ct = default);

    Task<BadgeCelebrationOutcome> MarkBadgeCelebrationShownAsync(
        Guid tenantId,
        Guid studentId,
        Guid badgeAwardId,
        CancellationToken ct = default);
}

public sealed record StudentProgressSummary(
    Guid StudentId,
    string CurriculumType,
    IReadOnlyList<MasterySubjectSummary> MasteryBySubject,
    StreakSummary Streak,
    IReadOnlyList<BadgeSummary> Badges,
    IReadOnlyList<FocusAreaSummary> FocusAreas);

public sealed record MasterySubjectSummary(
    Guid SubjectId,
    string SubjectLabelAr,
    string SubjectLabelEn,
    decimal MasteryScore,
    string MasteryBand,
    IReadOnlyList<MasteryTopicSummary> TopicBreakdown);

public sealed record MasteryTopicSummary(
    Guid TopicId,
    string TopicLabelAr,
    string TopicLabelEn,
    decimal MasteryScore,
    string MasteryBand);

public sealed record StreakSummary(
    int CurrentLength,
    int LongestLength,
    string? LastQualifyingDay,
    string FamilyTimezone);

public sealed record BadgeSummary(
    Guid BadgeAwardId,
    string BadgeKey,
    string BadgeCriterionVersion,
    DateTime AwardedAt,
    string DisplayNameAr,
    string DisplayNameEn,
    bool CelebrationShown);

public sealed record FocusAreaSummary(
    Guid FocusAreaId,
    Guid SubjectId,
    Guid ChapterId,
    Guid TopicId,
    string RationaleAr,
    string RationaleEn,
    FocusAreaNextStep SuggestedNextStep);

public sealed record FocusAreaNextStep(string Phase3Mode, string DeepLink);

public sealed record FocusAreaDetail(
    FocusAreaSummary Summary,
    string SignalSummary,
    DateTime ComputedAt,
    DateTime ValidUntil,
    string CorrelationId);

public enum BadgeCelebrationOutcome
{
    Marked,
    AlreadyShown,
    NotFound,
}

public sealed class StudentProgressService : IStudentProgressService
{
    private readonly MuallimiDbContext _db;
    private readonly ICurriculumLabelResolver _labels;

    public StudentProgressService(MuallimiDbContext db, ICurriculumLabelResolver labels)
    {
        _db = db;
        _labels = labels;
    }

    public async Task<StudentProgressSummary> BuildSummaryAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default)
    {
        var masteryRows = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.StudentId == studentId
                        && m.CalculationVersion == MasteryCalculator.Version)
            .ToListAsync(ct);

        var mastery = BuildMasteryBySubject(masteryRows);

        var streakRow = await _db.StreakStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.StudentId == studentId, ct);
        var streak = streakRow is null
            ? new StreakSummary(0, 0, null, "Asia/Dubai")
            : new StreakSummary(
                streakRow.CurrentLength,
                streakRow.LongestLength,
                streakRow.LastQualifyingDay.ToString("yyyy-MM-dd"),
                streakRow.FamilyTimezone);

        var badgeAwards = await _db.BadgeAwards
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.StudentId == studentId)
            .OrderByDescending(b => b.AwardedAt)
            .ToListAsync(ct);
        var criteria = await _db.BadgeCriteria
            .AsNoTracking()
            .ToListAsync(ct);
        var criterionLookup = criteria.ToDictionary(c => (c.BadgeCriterionId, c.Version));
        var badges = badgeAwards.Select(a =>
        {
            criterionLookup.TryGetValue((a.BadgeCriterionId, a.BadgeCriterionVersion), out var criterion);
            return new BadgeSummary(
                BadgeAwardId: a.BadgeAwardId,
                BadgeKey: criterion?.BadgeKey ?? a.BadgeCriterionId.ToString(),
                BadgeCriterionVersion: a.BadgeCriterionVersion,
                AwardedAt: a.AwardedAt,
                DisplayNameAr: criterion?.DisplayNameAr ?? a.BadgeCriterionId.ToString(),
                DisplayNameEn: criterion?.DisplayNameEn ?? a.BadgeCriterionId.ToString(),
                CelebrationShown: a.CelebrationShown);
        }).ToList();

        var focusRows = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId
                        && f.StudentId == studentId
                        && f.ValidUntil > DateTime.UtcNow)
            .OrderByDescending(f => f.ComputedAt)
            .ToListAsync(ct);
        var focusAreas = focusRows.Select(BuildFocusSummary).ToList();

        var curriculumType = masteryRows.FirstOrDefault()?.CurriculumType
                             ?? focusRows.FirstOrDefault()?.CurriculumType
                             ?? "moe";

        return new StudentProgressSummary(
            StudentId: studentId,
            CurriculumType: curriculumType,
            MasteryBySubject: mastery,
            Streak: streak,
            Badges: badges,
            FocusAreas: focusAreas);
    }

    public async Task<FocusAreaDetail?> GetFocusAreaDetailAsync(
        Guid tenantId,
        Guid studentId,
        Guid focusAreaId,
        CancellationToken ct = default)
    {
        var row = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.FocusAreaId == focusAreaId
                                      && f.TenantId == tenantId
                                      && f.StudentId == studentId, ct);
        if (row is null) return null;
        return new FocusAreaDetail(
            Summary: BuildFocusSummary(row),
            SignalSummary: row.SignalSummary,
            ComputedAt: row.ComputedAt,
            ValidUntil: row.ValidUntil,
            CorrelationId: row.CorrelationId);
    }

    public async Task<BadgeCelebrationOutcome> MarkBadgeCelebrationShownAsync(
        Guid tenantId,
        Guid studentId,
        Guid badgeAwardId,
        CancellationToken ct = default)
    {
        var row = await _db.BadgeAwards
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(b => b.BadgeAwardId == badgeAwardId
                                      && b.TenantId == tenantId
                                      && b.StudentId == studentId, ct);
        if (row is null) return BadgeCelebrationOutcome.NotFound;
        if (row.CelebrationShown) return BadgeCelebrationOutcome.AlreadyShown;
        row.CelebrationShown = true;
        await _db.SaveChangesAsync(ct);
        return BadgeCelebrationOutcome.Marked;
    }

    private IReadOnlyList<MasterySubjectSummary> BuildMasteryBySubject(IReadOnlyList<MasteryState> rows)
    {
        var bySubject = rows
            .GroupBy(r => r.SubjectId)
            .OrderBy(g => g.Key);

        var result = new List<MasterySubjectSummary>();
        foreach (var group in bySubject)
        {
            var rollup = group.FirstOrDefault(r => r.TopicId == null);
            var topics = group
                .Where(r => r.TopicId.HasValue)
                .OrderBy(r => r.TopicId)
                .Select(r =>
                {
                    var (ar, en) = _labels.ResolveTopic(r.SubjectId, r.TopicId!.Value);
                    return new MasteryTopicSummary(
                        TopicId: r.TopicId!.Value,
                        TopicLabelAr: ar,
                        TopicLabelEn: en,
                        MasteryScore: r.MasteryScore,
                        MasteryBand: r.MasteryBand);
                })
                .ToList();

            var (subjectAr, subjectEn) = _labels.ResolveSubject(group.Key);
            var score = rollup?.MasteryScore ?? (topics.Count == 0 ? 0m : topics.Average(t => t.MasteryScore));
            var band = rollup?.MasteryBand ?? MasteryCalculator.BandFor(score);
            result.Add(new MasterySubjectSummary(
                SubjectId: group.Key,
                SubjectLabelAr: subjectAr,
                SubjectLabelEn: subjectEn,
                MasteryScore: score,
                MasteryBand: band,
                TopicBreakdown: topics));
        }
        return result;
    }

    private static FocusAreaSummary BuildFocusSummary(FocusArea row)
    {
        var (mode, deepLink) = ParseNextStep(row.SuggestedNextStep);
        return new FocusAreaSummary(
            FocusAreaId: row.FocusAreaId,
            SubjectId: row.SubjectId,
            ChapterId: row.ChapterId,
            TopicId: row.TopicId,
            RationaleAr: row.RationaleAr,
            RationaleEn: row.RationaleEn,
            SuggestedNextStep: new FocusAreaNextStep(mode, deepLink));
    }

    private static (string Mode, string DeepLink) ParseNextStep(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return ("study", "/study");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ("study", "/study");
            var mode = doc.RootElement.TryGetProperty("phase3_mode", out var modeProp)
                ? modeProp.GetString() ?? "study" : "study";
            var deepLink = doc.RootElement.TryGetProperty("deep_link", out var linkProp)
                ? linkProp.GetString() ?? "/study" : "/study";
            return (mode, deepLink);
        }
        catch
        {
            return ("study", "/study");
        }
    }
}

public static class StudentProgressServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4StudentProgressService(this IServiceCollection services)
    {
        services.AddScoped<IStudentProgressService, StudentProgressService>();
        services.AddScoped<ICurriculumLabelResolver, DefaultCurriculumLabelResolver>();
        return services;
    }
}
