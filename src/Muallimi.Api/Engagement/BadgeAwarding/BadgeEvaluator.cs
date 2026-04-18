using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Api.Engagement.StreakCalculation;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.BadgeAwarding;

/// <summary>
/// T039 (US4) + T120 (US6) — Badge evaluator.
///
/// Walks the active <see cref="BadgeCriterion"/> catalogue and grants any
/// criterion the student satisfies, capturing the <c>badge_criterion_version</c>
/// at award time so future criterion tuning never retroactively invalidates an
/// existing badge. Evaluation is idempotent — the UNIQUE
/// <c>(tenant_id, student_id, badge_criterion_id, badge_criterion_version)</c>
/// constraint plus an existence check makes re-evaluation a no-op.
///
/// T120: every freshly-inserted award starts with <c>CelebrationShown = false</c>
/// and is returned to the caller through <see cref="BadgeEvaluationResult"/>
/// so the surface can fire its non-blocking affordance on next render. The
/// dedicated result type makes the celebration handoff explicit (the legacy
/// <c>EvaluateAsync</c> overload is retained for callers that only need the
/// awards list).
///
/// v1 supports four threshold types:
/// <list type="bullet">
///   <item><description><c>streak</c>: <c>days</c> minimum trailing-run length</description></item>
///   <item><description><c>quiz_accuracy</c>: <c>min_correct_pct</c> over <c>min_questions</c> recent quiz_answered events</description></item>
///   <item><description><c>topic_coverage</c>: <c>completion_pct</c> = mastery >= 0.5 in any topic-scoped MasteryState</description></item>
///   <item><description><c>mastery_band_jump</c>: <c>bands</c> band increase across consecutive recompute snapshots</description></item>
/// </list>
/// </summary>
public interface IBadgeEvaluator
{
    Task<IReadOnlyList<BadgeAward>> EvaluateAsync(
        Guid tenantId,
        Guid studentId,
        Guid originatingProgressRecordId,
        string correlationId,
        BadgeEvaluationContext context,
        CancellationToken ct = default);

    Task<BadgeEvaluationResult> EvaluateWithCelebrationAsync(
        Guid tenantId,
        Guid studentId,
        Guid originatingProgressRecordId,
        string correlationId,
        BadgeEvaluationContext context,
        CancellationToken ct = default);
}

public sealed record BadgeEvaluationResult(
    IReadOnlyList<BadgeAward> NewlyAwarded,
    IReadOnlyList<Guid> CelebrationCandidates)
{
    public static readonly BadgeEvaluationResult Empty =
        new(Array.Empty<BadgeAward>(), Array.Empty<Guid>());
}

public sealed record BadgeEvaluationContext(
    int CurrentStreakLength,
    IReadOnlyList<MasteryState> CurrentMasteryStates,
    string PriorMasteryBand,
    string NewMasteryBand);

public sealed class BadgeEvaluator : IBadgeEvaluator
{
    private readonly IBadgeCriterionRepository _criteria;
    private readonly IBadgeAwardRepository _awards;
    private readonly IProgressRecordRepository _records;

    public BadgeEvaluator(
        IBadgeCriterionRepository criteria,
        IBadgeAwardRepository awards,
        IProgressRecordRepository records)
    {
        _criteria = criteria;
        _awards = awards;
        _records = records;
    }

    public async Task<IReadOnlyList<BadgeAward>> EvaluateAsync(
        Guid tenantId,
        Guid studentId,
        Guid originatingProgressRecordId,
        string correlationId,
        BadgeEvaluationContext context,
        CancellationToken ct = default)
    {
        var result = await EvaluateWithCelebrationAsync(
            tenantId, studentId, originatingProgressRecordId, correlationId, context, ct);
        return result.NewlyAwarded;
    }

    public async Task<BadgeEvaluationResult> EvaluateWithCelebrationAsync(
        Guid tenantId,
        Guid studentId,
        Guid originatingProgressRecordId,
        string correlationId,
        BadgeEvaluationContext context,
        CancellationToken ct = default)
    {
        var awarded = new List<BadgeAward>();
        var celebrationCandidates = new List<Guid>();
        var active = await _criteria.ActiveAsync(ct);

        foreach (var criterion in active)
        {
            if (await _awards.ExistsAsync(tenantId, studentId, criterion.BadgeCriterionId, criterion.Version, ct))
            {
                continue;
            }
            if (!await SatisfiesAsync(criterion, tenantId, studentId, context, ct))
            {
                continue;
            }
            var award = new BadgeAward
            {
                BadgeAwardId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                BadgeCriterionId = criterion.BadgeCriterionId,
                BadgeCriterionVersion = criterion.Version,
                AwardedAt = DateTime.UtcNow,
                OriginatingProgressRecordIds = JsonSerializer.Serialize(new[] { originatingProgressRecordId }),
                // T120: celebration_shown is always false at award time. The
                // student progress surface flips it via
                // POST /student/progress/badges/{id}/celebration-shown after
                // the non-blocking affordance has rendered once.
                CelebrationShown = false,
                CorrelationId = correlationId,
            };
            await _awards.AddAsync(award, ct);
            awarded.Add(award);
            celebrationCandidates.Add(award.BadgeAwardId);
        }

        return awarded.Count == 0
            ? BadgeEvaluationResult.Empty
            : new BadgeEvaluationResult(awarded, celebrationCandidates);
    }

    private async Task<bool> SatisfiesAsync(
        BadgeCriterion criterion,
        Guid tenantId,
        Guid studentId,
        BadgeEvaluationContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(criterion.Threshold) || criterion.Threshold == "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(criterion.Threshold);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return false;
            var type = typeProp.GetString();
            return type switch
            {
                "streak" => SatisfiesStreak(doc.RootElement, context),
                "quiz_accuracy" => await SatisfiesQuizAccuracyAsync(doc.RootElement, tenantId, studentId, ct),
                "topic_coverage" => SatisfiesTopicCoverage(doc.RootElement, context),
                "mastery_band_jump" => SatisfiesBandJump(doc.RootElement, context),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    private static bool SatisfiesStreak(JsonElement threshold, BadgeEvaluationContext ctx)
    {
        if (!threshold.TryGetProperty("days", out var daysProp)) return false;
        var days = daysProp.GetInt32();
        return ctx.CurrentStreakLength >= days;
    }

    private async Task<bool> SatisfiesQuizAccuracyAsync(
        JsonElement threshold,
        Guid tenantId,
        Guid studentId,
        CancellationToken ct)
    {
        if (!threshold.TryGetProperty("min_correct_pct", out var pctProp)) return false;
        if (!threshold.TryGetProperty("min_questions", out var minQProp)) return false;
        var minPct = pctProp.GetDecimal();
        var minQ = minQProp.GetInt32();

        var prs = await _records.ForStudentAsync(tenantId, studentId, ct);
        var quizzes = prs.Where(r => r.EventKind == Phase3EventKinds.QuizAnswered).ToList();
        if (quizzes.Count < minQ) return false;
        var correct = quizzes.Count(r => ReadBool(r.Payload, "is_correct"));
        var pct = (decimal)correct / quizzes.Count;
        return pct >= minPct;
    }

    private static bool SatisfiesTopicCoverage(JsonElement threshold, BadgeEvaluationContext ctx)
    {
        if (!threshold.TryGetProperty("completion_pct", out _)) return false;
        return ctx.CurrentMasteryStates.Any(s => s.TopicId.HasValue && s.MasteryScore >= 0.5m);
    }

    private static bool SatisfiesBandJump(JsonElement threshold, BadgeEvaluationContext ctx)
    {
        if (!threshold.TryGetProperty("bands", out var bandsProp)) return false;
        var bands = bandsProp.GetInt32();
        return BandRank(ctx.NewMasteryBand) - BandRank(ctx.PriorMasteryBand) >= bands;
    }

    private static int BandRank(string band) => band switch
    {
        MasteryCalculator.BandIntroduced => 0,
        MasteryCalculator.BandPracticing => 1,
        MasteryCalculator.BandOnTrack => 2,
        MasteryCalculator.BandConfident => 3,
        _ => 0,
    };

    private static bool ReadBool(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty(key, out var prop)) return false;
            return prop.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}

public static class BadgeEvaluatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4BadgeEvaluator(this IServiceCollection services)
    {
        services.AddScoped<IBadgeEvaluator, BadgeEvaluator>();
        return services;
    }
}
