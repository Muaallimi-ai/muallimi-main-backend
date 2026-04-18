using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T091 (US3) — Weekly report aggregator.
///
/// Rolls up the window's mastery deltas, top focus areas, awarded badges,
/// and evidence references for a single student. Pure read-only: builds
/// an immutable <see cref="WeeklyReportAggregate"/> that the generator
/// uses as the guardrail-chain grounding and that the stored row
/// serialises into its jsonb columns.
/// </summary>
public interface IWeeklyReportAggregator
{
    Task<WeeklyReportAggregate> AggregateAsync(
        Guid tenantId,
        Guid studentId,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken ct = default);
}

public sealed record WeeklyReportAggregate(
    IReadOnlyList<WeeklyMasteryDelta> MasteryDeltas,
    IReadOnlyList<WeeklyFocusAreaSnapshot> TopFocusAreas,
    IReadOnlyList<WeeklyBadgeAwardSnapshot> AwardedBadges,
    IReadOnlyList<WeeklyEvidenceRef> EvidenceRefs);

public sealed record WeeklyMasteryDelta(
    Guid SubjectId,
    Guid? TopicId,
    decimal PriorScore,
    decimal NewScore,
    string Band);

public sealed record WeeklyFocusAreaSnapshot(
    Guid FocusAreaId,
    Guid SubjectId,
    Guid TopicId,
    string RationaleAr,
    string RationaleEn,
    string Phase3Mode,
    string DeepLink);

public sealed record WeeklyBadgeAwardSnapshot(
    Guid BadgeAwardId,
    string BadgeKey,
    string BadgeCriterionVersion,
    string DisplayNameAr,
    string DisplayNameEn);

public sealed record WeeklyEvidenceRef(
    Guid ProgressRecordId,
    string SourceEventId,
    string CurriculumScope);

public sealed class WeeklyReportAggregator : IWeeklyReportAggregator
{
    private readonly MuallimiDbContext _db;

    public WeeklyReportAggregator(MuallimiDbContext db) => _db = db;

    public async Task<WeeklyReportAggregate> AggregateAsync(
        Guid tenantId,
        Guid studentId,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken ct = default)
    {
        var startUtc = DateTime.SpecifyKind(windowStart.Date, DateTimeKind.Utc);
        var endExclusive = DateTime.SpecifyKind(windowEnd.Date.AddDays(1), DateTimeKind.Utc);

        var mastery = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.StudentId == studentId)
            .ToListAsync(ct);

        var deltas = mastery
            .Where(m => m.LastUpdatedAt >= startUtc && m.LastUpdatedAt < endExclusive)
            .Select(m => new WeeklyMasteryDelta(
                SubjectId: m.SubjectId,
                TopicId: m.TopicId,
                PriorScore: 0m,
                NewScore: m.MasteryScore,
                Band: m.MasteryBand))
            .ToList();

        var focusRows = await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId
                        && f.StudentId == studentId
                        && f.ComputedAt < endExclusive
                        && f.ValidUntil >= startUtc)
            .OrderByDescending(f => f.ComputedAt)
            .Take(3)
            .ToListAsync(ct);
        var focus = focusRows.Select(BuildFocus).ToList();

        var awardRows = await _db.BadgeAwards
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId
                        && a.StudentId == studentId
                        && a.AwardedAt >= startUtc
                        && a.AwardedAt < endExclusive)
            .OrderBy(a => a.AwardedAt)
            .ToListAsync(ct);

        var criterionIds = awardRows.Select(a => a.BadgeCriterionId).Distinct().ToArray();
        var criteria = await _db.BadgeCriteria
            .Where(c => criterionIds.Contains(c.BadgeCriterionId))
            .ToDictionaryAsync(c => c.BadgeCriterionId, ct);

        var badges = awardRows
            .Select(a => criteria.TryGetValue(a.BadgeCriterionId, out var c)
                ? new WeeklyBadgeAwardSnapshot(a.BadgeAwardId, c.BadgeKey, a.BadgeCriterionVersion, c.DisplayNameAr, c.DisplayNameEn)
                : new WeeklyBadgeAwardSnapshot(a.BadgeAwardId, string.Empty, a.BadgeCriterionVersion, string.Empty, string.Empty))
            .ToList();

        var evidenceRows = await _db.ProgressRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId
                        && p.StudentId == studentId
                        && p.OccurredAt >= startUtc
                        && p.OccurredAt < endExclusive)
            .OrderBy(p => p.OccurredAt)
            .ToListAsync(ct);
        var evidence = evidenceRows
            .Select(p => new WeeklyEvidenceRef(p.ProgressRecordId, p.SourceEventId, p.CurriculumScope))
            .ToList();

        return new WeeklyReportAggregate(deltas, focus, badges, evidence);
    }

    private static WeeklyFocusAreaSnapshot BuildFocus(FocusArea row)
    {
        var (mode, link) = ParseNextStep(row.SuggestedNextStep);
        return new WeeklyFocusAreaSnapshot(
            FocusAreaId: row.FocusAreaId,
            SubjectId: row.SubjectId,
            TopicId: row.TopicId,
            RationaleAr: row.RationaleAr,
            RationaleEn: row.RationaleEn,
            Phase3Mode: mode,
            DeepLink: link);
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
            var link = doc.RootElement.TryGetProperty("deep_link", out var l)
                ? l.GetString() ?? "/study" : "/study";
            return (mode, link);
        }
        catch
        {
            return ("study", "/study");
        }
    }
}

public static class WeeklyReportAggregatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4WeeklyReportAggregator(this IServiceCollection services)
    {
        services.AddScoped<IWeeklyReportAggregator, WeeklyReportAggregator>();
        return services;
    }
}
