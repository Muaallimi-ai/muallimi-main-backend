using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.MasteryCalculation;

/// <summary>
/// T037 (US4) — Mastery calculator (calculation_version = v1).
///
/// Applies a reproducible, order-independent weighted sum over the student's
/// <see cref="ProgressRecord"/> rows for a (subject, topic) scope. Weights
/// are documented in <c>research.md</c>:
/// <list type="bullet">
///   <item><description><c>lesson_view</c>          +0.05</description></item>
///   <item><description><c>content_play</c>         +0.05</description></item>
///   <item><description><c>quiz_answered</c>      ±0.10/0.05  depending on correctness</description></item>
///   <item><description><c>mock_test</c>             +0.08 per correct item</description></item>
///   <item><description><c>homework_help_used</c>    +0.02</description></item>
///   <item><description><c>whiteboard_session</c>    +0.06</description></item>
/// </list>
///
/// The score is clamped to <c>[0.0, 1.0]</c> (floor 0, ceiling 1). The band
/// is derived from the clamped score with fixed thresholds so a parent-facing
/// label stays stable across recomputation.
///
/// Reproducibility invariant (covered by T032): recomputing from the stored
/// <see cref="ProgressRecord"/> set MUST yield the same score as the
/// incremental pipeline — guaranteed here because the calculation is a pure
/// function of the PR set and clamping is applied only once to the summed raw
/// score.
/// </summary>
public interface IMasteryCalculator
{
    string CalculationVersion { get; }

    Task<MasteryRecomputeResult> RecomputeAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid? topicId,
        string curriculumType,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record MasteryRecomputeResult(
    MasteryState State,
    decimal PriorScore,
    string PriorBand,
    decimal NewScore,
    string NewBand,
    bool Changed);

public sealed class MasteryCalculator : IMasteryCalculator
{
    public const string Version = "v1";

    public const string BandIntroduced = "introduced";
    public const string BandPracticing = "practicing";
    public const string BandOnTrack = "on_track";
    public const string BandConfident = "confident";

    private readonly IProgressRecordRepository _records;
    private readonly IMasteryStateRepository _states;

    public MasteryCalculator(
        IProgressRecordRepository records,
        IMasteryStateRepository states)
    {
        _records = records;
        _states = states;
    }

    public string CalculationVersion => Version;

    public async Task<MasteryRecomputeResult> RecomputeAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid? topicId,
        string curriculumType,
        string correlationId,
        CancellationToken ct = default)
    {
        var prs = await _records.ForStudentScopeAsync(tenantId, studentId, subjectId, topicId, ct);
        var raw = 0m;
        DateTime? first = null;
        DateTime? last = null;

        foreach (var r in prs)
        {
            raw += WeightFor(r);
            if (first is null || r.OccurredAt < first) first = r.OccurredAt;
            if (last is null || r.OccurredAt > last) last = r.OccurredAt;
        }

        var score = Clamp(raw);
        var band = BandFor(score);

        var existing = await _states.GetAsync(tenantId, studentId, subjectId, topicId, Version, ct);
        var priorScore = existing?.MasteryScore ?? 0m;
        var priorBand = existing?.MasteryBand ?? BandIntroduced;

        if (existing is null)
        {
            var row = new MasteryState
            {
                MasteryStateId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                SubjectId = subjectId,
                TopicId = topicId,
                CurriculumType = curriculumType,
                MasteryScore = score,
                MasteryBand = band,
                CalculationVersion = Version,
                SampleWindowStart = first,
                SampleWindowEnd = last,
                ContributingRecordCount = prs.Count,
                LastUpdatedAt = DateTime.UtcNow,
                LastCorrelationId = correlationId,
            };
            await _states.AddAsync(row, ct);
            var changed = prs.Count > 0;
            return new MasteryRecomputeResult(row, priorScore, priorBand, score, band, changed);
        }
        else
        {
            var changed = existing.MasteryScore != score
                          || existing.MasteryBand != band
                          || existing.ContributingRecordCount != prs.Count;
            existing.MasteryScore = score;
            existing.MasteryBand = band;
            existing.SampleWindowStart = first;
            existing.SampleWindowEnd = last;
            existing.ContributingRecordCount = prs.Count;
            existing.LastUpdatedAt = DateTime.UtcNow;
            existing.LastCorrelationId = correlationId;
            return new MasteryRecomputeResult(existing, priorScore, priorBand, score, band, changed);
        }
    }

    internal static decimal WeightFor(ProgressRecord record)
    {
        switch (record.EventKind)
        {
            case Phase3EventKinds.LessonView: return 0.05m;
            case Phase3EventKinds.ContentPlay: return 0.05m;
            case Phase3EventKinds.HomeworkHelpUsed: return 0.02m;
            case Phase3EventKinds.WhiteboardSession: return 0.06m;
            case Phase3EventKinds.QuizAnswered:
                return ReadBool(record.Payload, "is_correct") ? 0.10m : -0.05m;
            case Phase3EventKinds.MockTest:
                return 0.08m * ReadInt(record.Payload, "correct_count", 0);
            default:
                return 0m;
        }
    }

    internal static decimal Clamp(decimal raw)
    {
        if (raw < 0m) return 0m;
        if (raw > 1m) return 1m;
        return raw;
    }

    internal static string BandFor(decimal score)
    {
        if (score < 0.25m) return BandIntroduced;
        if (score < 0.5m) return BandPracticing;
        if (score < 0.75m) return BandOnTrack;
        return BandConfident;
    }

    private static bool ReadBool(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(key, out var prop)) return false;
            return prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    private static int ReadInt(string json, string key, int fallback)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return fallback;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return fallback;
            if (!doc.RootElement.TryGetProperty(key, out var prop)) return fallback;
            return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}

public static class MasteryCalculatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4MasteryCalculator(this IServiceCollection services)
    {
        services.AddScoped<IMasteryCalculator, MasteryCalculator>();
        return services;
    }
}
