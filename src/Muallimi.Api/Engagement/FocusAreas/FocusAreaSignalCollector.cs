using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.FocusAreas;

/// <summary>
/// T108 (US5) — FocusAreaSignalCollector.
///
/// Walks the student's <see cref="ProgressRecord"/> rows, derives touched
/// curriculum nodes (subject + chapter + topic), and marries those nodes
/// against the current <see cref="MasteryState"/>. For each touched node
/// the collector produces a single <see cref="FocusAreaSignal"/> carrying:
///
///   - mastery gap magnitude (<c>1 - mastery_score</c>), clamped to
///     <c>[0, 1]</c> and zero when band &gt;= on_track;
///   - quiz error count (<c>quiz_answered</c> with <c>is_correct = false</c>);
///   - homework-help signal count (<c>homework_help_used</c>).
///
/// Every signal is anchored to a node the student actually touched —
/// orphan mastery rows or untouched topics are never surfaced, satisfying
/// the T103 grounding invariant.
/// </summary>
public interface IFocusAreaSignalCollector
{
    Task<IReadOnlyList<FocusAreaSignal>> CollectAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default);
}

public sealed record FocusAreaSignal(
    string CurriculumType,
    Guid SubjectId,
    Guid ChapterId,
    Guid TopicId,
    decimal MasteryGap,
    string MasteryBand,
    int QuizErrorCount,
    int HomeworkHelpCount,
    int TouchedEventCount,
    IReadOnlyList<Guid> OriginatingProgressRecordIds)
{
    public decimal Score =>
        MasteryGap
        + 0.1m * QuizErrorCount
        + 0.05m * HomeworkHelpCount;
}

public sealed class FocusAreaSignalCollector : IFocusAreaSignalCollector
{
    private readonly MuallimiDbContext _db;

    public FocusAreaSignalCollector(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<FocusAreaSignal>> CollectAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default)
    {
        var records = await _db.ProgressRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.StudentId == studentId)
            .ToListAsync(ct);

        if (records.Count == 0) return Array.Empty<FocusAreaSignal>();

        var mastery = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.StudentId == studentId
                        && m.CalculationVersion == MasteryCalculator.Version)
            .ToListAsync(ct);

        var masteryByScope = mastery
            .Where(m => m.TopicId.HasValue)
            .GroupBy(m => (m.SubjectId, m.TopicId!.Value))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.LastUpdatedAt).First());

        var grouped = new Dictionary<FocusAreaScopeKey, SignalAccumulator>();
        foreach (var record in records)
        {
            if (!TryParseScope(record.CurriculumScope, out var curriculumType, out var subjectId, out var chapterId, out var topicId))
            {
                continue;
            }

            var key = new FocusAreaScopeKey(curriculumType, subjectId, chapterId, topicId);
            if (!grouped.TryGetValue(key, out var acc))
            {
                acc = new SignalAccumulator();
                grouped[key] = acc;
            }

            acc.TouchedEventCount++;
            acc.OriginatingIds.Add(record.ProgressRecordId);

            switch (record.EventKind)
            {
                case Phase3EventKinds.QuizAnswered:
                    if (!ReadBool(record.Payload, "is_correct")) acc.QuizErrorCount++;
                    break;
                case Phase3EventKinds.HomeworkHelpUsed:
                    acc.HomeworkHelpCount++;
                    break;
            }
        }

        var results = new List<FocusAreaSignal>(grouped.Count);
        foreach (var (key, acc) in grouped)
        {
            var band = MasteryCalculator.BandIntroduced;
            var score = 0m;
            if (masteryByScope.TryGetValue((key.SubjectId, key.TopicId), out var m))
            {
                band = m.MasteryBand;
                score = m.MasteryScore;
            }

            var gap = band switch
            {
                MasteryCalculator.BandIntroduced => ClampGap(1m - score),
                MasteryCalculator.BandPracticing => ClampGap(1m - score),
                _ => 0m,
            };

            var hasGapSignal = gap > 0m;
            var hasErrorSignal = acc.QuizErrorCount > 0;
            var hasHomeworkSignal = acc.HomeworkHelpCount > 0;
            if (!hasGapSignal && !hasErrorSignal && !hasHomeworkSignal) continue;

            results.Add(new FocusAreaSignal(
                CurriculumType: key.CurriculumType,
                SubjectId: key.SubjectId,
                ChapterId: key.ChapterId,
                TopicId: key.TopicId,
                MasteryGap: gap,
                MasteryBand: band,
                QuizErrorCount: acc.QuizErrorCount,
                HomeworkHelpCount: acc.HomeworkHelpCount,
                TouchedEventCount: acc.TouchedEventCount,
                OriginatingProgressRecordIds: acc.OriginatingIds));
        }

        return results
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    internal static bool TryParseScope(
        string json,
        out string curriculumType,
        out Guid subjectId,
        out Guid chapterId,
        out Guid topicId)
    {
        curriculumType = "moe";
        subjectId = Guid.Empty;
        chapterId = Guid.Empty;
        topicId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("curriculum_type", out var ct) && ct.ValueKind == JsonValueKind.String)
            {
                curriculumType = ct.GetString() ?? "moe";
            }
            if (!TryReadGuid(root, "subject_id", out subjectId)) return false;
            if (!TryReadGuid(root, "topic_id", out topicId)) return false;
            if (!TryReadGuid(root, "chapter_id", out chapterId))
            {
                // Chapter may be absent in older envelopes; skip orphans so
                // every emitted FocusArea resolves to a complete Phase 1 node.
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        if (!root.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.String => Guid.TryParse(el.GetString(), out value),
            _ => false,
        };
    }

    private static bool ReadBool(string json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(property, out var el)) return false;
            return el.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static decimal ClampGap(decimal gap)
    {
        if (gap < 0m) return 0m;
        if (gap > 1m) return 1m;
        return gap;
    }

    private sealed record FocusAreaScopeKey(string CurriculumType, Guid SubjectId, Guid ChapterId, Guid TopicId);

    private sealed class SignalAccumulator
    {
        public int TouchedEventCount;
        public int QuizErrorCount;
        public int HomeworkHelpCount;
        public List<Guid> OriginatingIds { get; } = new();
    }
}

public static class FocusAreaSignalCollectorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4FocusAreaSignalCollector(this IServiceCollection services)
    {
        services.AddScoped<IFocusAreaSignalCollector, FocusAreaSignalCollector>();
        return services;
    }
}
