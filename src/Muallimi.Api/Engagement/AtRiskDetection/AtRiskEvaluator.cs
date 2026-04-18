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

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T145 (US8) — AtRiskEvaluator.
///
/// Evaluates a single student against the current
/// <see cref="AtRiskThresholdSet"/> using rolling-window signals derived
/// from <see cref="MasteryState"/> + <see cref="ProgressRecord"/> rows.
/// Returns a structured verdict so the orchestrator
/// (<see cref="AtRiskDetectionJob"/>) can decide whether to raise, clear,
/// or no-op the flag for the student.
///
/// False-positive bound (contract invariant): the evaluator never raises
/// unless at least one signal exceeds the documented threshold for that
/// criterion. Recovery uses looser ceilings to avoid oscillation.
/// </summary>
public interface IAtRiskEvaluator
{
    Task<AtRiskEvaluation> EvaluateAsync(
        Guid tenantId,
        Guid studentId,
        AtRiskThresholdSet thresholds,
        CancellationToken ct = default);
}

public sealed record AtRiskEvaluation(
    bool ExceedsThreshold,
    bool RecoveryAchieved,
    AtRiskTriggeringEvidence Evidence,
    string ThresholdVersion);

public sealed record AtRiskTriggeringEvidence(
    bool SustainedLowMastery,
    decimal LowestMasteryScore,
    Guid? LowestMasterySubjectId,
    Guid? LowestMasteryTopicId,
    bool RepeatedRefusal,
    int MaxRefusalCountOnTopic,
    Guid? RepeatedRefusalTopicId,
    bool DeclinedEngagement,
    int RecentEventCount,
    int PriorEventCount,
    bool FailedMockTests,
    int FailedMockTestCount,
    int SuccessfulMockTestCount)
{
    public string ToJson()
    {
        return JsonSerializer.Serialize(new
        {
            sustained_low_mastery = SustainedLowMastery,
            lowest_mastery_score = LowestMasteryScore,
            lowest_mastery_subject_id = LowestMasterySubjectId,
            lowest_mastery_topic_id = LowestMasteryTopicId,
            repeated_refusal = RepeatedRefusal,
            max_refusal_count_on_topic = MaxRefusalCountOnTopic,
            repeated_refusal_topic_id = RepeatedRefusalTopicId,
            declined_engagement = DeclinedEngagement,
            recent_event_count = RecentEventCount,
            prior_event_count = PriorEventCount,
            failed_mock_tests = FailedMockTests,
            failed_mock_test_count = FailedMockTestCount,
            successful_mock_test_count = SuccessfulMockTestCount,
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
    }
}

public sealed class AtRiskEvaluator : IAtRiskEvaluator
{
    private readonly MuallimiDbContext _db;

    public AtRiskEvaluator(MuallimiDbContext db) => _db = db;

    public async Task<AtRiskEvaluation> EvaluateAsync(
        Guid tenantId,
        Guid studentId,
        AtRiskThresholdSet thresholds,
        CancellationToken ct = default)
    {
        var mastery = await _db.MasteryStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId
                        && m.StudentId == studentId
                        && m.CalculationVersion == MasteryCalculator.Version)
            .ToListAsync(ct);

        var records = await _db.ProgressRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.StudentId == studentId)
            .OrderByDescending(r => r.OccurredAt)
            .Take(thresholds.RecentEventLookbackCount * 2)
            .ToListAsync(ct);

        var (sustainedLowMastery, lowestScore, lowestSubject, lowestTopic) =
            EvaluateSustainedLowMastery(mastery, thresholds);

        var (repeatedRefusal, maxRefusal, refusalTopic) =
            EvaluateRepeatedRefusal(records, thresholds);

        var (declinedEngagement, recentCount, priorCount) =
            EvaluateEngagementDecline(records, thresholds);

        var (failedMockTests, failedCount, successCount) =
            EvaluateFailedMockTests(records, thresholds);

        var evidence = new AtRiskTriggeringEvidence(
            SustainedLowMastery: sustainedLowMastery,
            LowestMasteryScore: lowestScore,
            LowestMasterySubjectId: lowestSubject,
            LowestMasteryTopicId: lowestTopic,
            RepeatedRefusal: repeatedRefusal,
            MaxRefusalCountOnTopic: maxRefusal,
            RepeatedRefusalTopicId: refusalTopic,
            DeclinedEngagement: declinedEngagement,
            RecentEventCount: recentCount,
            PriorEventCount: priorCount,
            FailedMockTests: failedMockTests,
            FailedMockTestCount: failedCount,
            SuccessfulMockTestCount: successCount);

        var exceedsThreshold = sustainedLowMastery
                               || repeatedRefusal
                               || declinedEngagement
                               || failedMockTests;

        var recoveryAchieved = !exceedsThreshold
                               && (mastery.Any() || records.Any())
                               && (lowestScore == 0m || lowestScore >= thresholds.RecoveryMasteryScoreFloor)
                               && successCount >= thresholds.RecoverySuccessfulMockTestCount;

        // If there is no data at all, we cannot claim recovery — leave both
        // signals false. Active-flag clearing is decided in the job.
        if (mastery.Count == 0 && records.Count == 0)
        {
            recoveryAchieved = false;
        }

        return new AtRiskEvaluation(
            ExceedsThreshold: exceedsThreshold,
            RecoveryAchieved: recoveryAchieved,
            Evidence: evidence,
            ThresholdVersion: thresholds.Version);
    }

    private static (bool, decimal, Guid?, Guid?) EvaluateSustainedLowMastery(
        IReadOnlyList<MasteryState> mastery, AtRiskThresholdSet thresholds)
    {
        if (mastery.Count == 0) return (false, 0m, null, null);

        MasteryState? lowest = null;
        foreach (var m in mastery)
        {
            if (lowest is null || m.MasteryScore < lowest.MasteryScore)
            {
                lowest = m;
            }
        }

        if (lowest is null) return (false, 0m, null, null);

        var sustained = lowest.MasteryScore <= thresholds.LowMasteryScoreCeiling
                        && lowest.ContributingRecordCount >= thresholds.SustainedLowMasteryWindowEvents;
        return (sustained, lowest.MasteryScore, lowest.SubjectId, lowest.TopicId);
    }

    private static (bool, int, Guid?) EvaluateRepeatedRefusal(
        IReadOnlyList<ProgressRecord> records, AtRiskThresholdSet thresholds)
    {
        var byTopic = new Dictionary<Guid, int>();
        foreach (var r in records.Where(r => r.EventKind == Phase3EventKinds.Refusal))
        {
            if (!TryReadGuid(r.CurriculumScope, "topic_id", out var topicId)) continue;
            byTopic[topicId] = byTopic.TryGetValue(topicId, out var c) ? c + 1 : 1;
        }

        if (byTopic.Count == 0) return (false, 0, null);
        var top = byTopic.OrderByDescending(kv => kv.Value).First();
        var triggers = top.Value >= thresholds.RepeatedRefusalCountOnTopic;
        return (triggers, top.Value, top.Key);
    }

    private static (bool, int, int) EvaluateEngagementDecline(
        IReadOnlyList<ProgressRecord> records, AtRiskThresholdSet thresholds)
    {
        var ordered = records.OrderByDescending(r => r.OccurredAt).ToList();
        var window = thresholds.RecentEventLookbackCount;
        var recent = ordered.Take(window).Count();
        var prior = ordered.Skip(window).Take(window).Count();

        if (prior == 0) return (false, recent, prior);
        var ratio = (decimal)recent / prior;
        var declined = ratio <= thresholds.EngagementDeclineRatio && prior >= window / 2;
        return (declined, recent, prior);
    }

    private static (bool, int, int) EvaluateFailedMockTests(
        IReadOnlyList<ProgressRecord> records, AtRiskThresholdSet thresholds)
    {
        var failed = 0;
        var successful = 0;
        foreach (var r in records.Where(r => r.EventKind == Phase3EventKinds.MockTest))
        {
            if (TryReadBool(r.Payload, "passed", out var passed))
            {
                if (passed) successful++;
                else failed++;
            }
        }
        return (failed >= thresholds.FailedMockTestCount, failed, successful);
    }

    private static bool TryReadGuid(string json, string property, out Guid value)
    {
        value = Guid.Empty;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(property, out var el)) return false;
            return el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBool(string json, string property, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(property, out var el)) return false;
            if (el.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (el.ValueKind == JsonValueKind.False) { value = false; return true; }
            return false;
        }
        catch
        {
            return false;
        }
    }
}

public static class AtRiskEvaluatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4AtRiskEvaluator(this IServiceCollection services)
    {
        services.AddScoped<IAtRiskEvaluator, AtRiskEvaluator>();
        return services;
    }
}
