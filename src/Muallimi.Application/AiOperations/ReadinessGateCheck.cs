namespace Muallimi.Application.AiOperations;

/// <summary>
/// T106 (US6, FR-021) — Blocks promotion when any of the readiness targets
/// are missed for the current aggregation window. Targets come from spec
/// SC-001/003/005/009/010/013 and are configurable at build time. The gate
/// runs on the persisted <see cref="AggregatedMetric"/> surface so the same
/// rows that power the operator dashboard power promotion decisions — no
/// parallel data path, no drift.
/// </summary>
public class ReadinessGateCheck
{
    public const string TargetCostPerQuestion = "cost_per_question";
    public const string TargetCacheHitRate = "cache_hit_rate";
    public const string TargetRefusalRate = "refusal_rate";
    public const string TargetLatencyP95 = "latency_p95_ms";
    public const string TargetGroundedAnswerRate = "grounded_answer_rate";

    private readonly ReadinessTargets _targets;

    public ReadinessGateCheck(ReadinessTargets? targets = null)
    {
        _targets = targets ?? ReadinessTargets.Default;
    }

    public ReadinessTargets Targets => _targets;

    public ReadinessGateResult Evaluate(AggregatedMetric metric, double observedLatencyP95Ms)
    {
        var failures = new List<ReadinessFailure>();

        var costPerQuestion = ComputeCostPerQuestion(metric);
        if (metric.Volume > 0 && costPerQuestion > _targets.MaxCostPerQuestion)
            failures.Add(new ReadinessFailure(
                Target: TargetCostPerQuestion,
                Observed: costPerQuestion,
                Threshold: _targets.MaxCostPerQuestion,
                Message: $"Cost per question {costPerQuestion:F4} exceeds budget {_targets.MaxCostPerQuestion:F4}."));

        if (metric.Volume > 0 && metric.CacheHitRate < _targets.MinCacheHitRate)
            failures.Add(new ReadinessFailure(
                Target: TargetCacheHitRate,
                Observed: metric.CacheHitRate,
                Threshold: _targets.MinCacheHitRate,
                Message: $"Cache hit rate {metric.CacheHitRate:P1} below target {_targets.MinCacheHitRate:P1}."));

        if (metric.Volume > 0 && metric.RefusalRate > _targets.MaxRefusalRate)
            failures.Add(new ReadinessFailure(
                Target: TargetRefusalRate,
                Observed: metric.RefusalRate,
                Threshold: _targets.MaxRefusalRate,
                Message: $"Refusal rate {metric.RefusalRate:P1} exceeds ceiling {_targets.MaxRefusalRate:P1}."));

        if (metric.Volume > 0 && observedLatencyP95Ms > _targets.MaxLatencyP95Ms)
            failures.Add(new ReadinessFailure(
                Target: TargetLatencyP95,
                Observed: observedLatencyP95Ms,
                Threshold: _targets.MaxLatencyP95Ms,
                Message: $"Latency p95 {observedLatencyP95Ms:F0}ms exceeds target {_targets.MaxLatencyP95Ms:F0}ms."));

        if (metric.Volume > 0 && metric.GroundedAnswerRate < _targets.MinGroundedAnswerRate)
            failures.Add(new ReadinessFailure(
                Target: TargetGroundedAnswerRate,
                Observed: metric.GroundedAnswerRate,
                Threshold: _targets.MinGroundedAnswerRate,
                Message: $"Grounded-answer rate {metric.GroundedAnswerRate:P1} below target {_targets.MinGroundedAnswerRate:P1}."));

        return new ReadinessGateResult(
            PromotionBlocked: failures.Count > 0,
            Failures: failures,
            Metric: metric,
            LatencyP95Ms: observedLatencyP95Ms,
            CostPerQuestion: costPerQuestion);
    }

    public static double ComputeCostPerQuestion(AggregatedMetric metric)
    {
        if (metric.Volume == 0) return 0;
        var totalCost = metric.PerBranch.Values.Sum(b => b.CostTotal);
        return totalCost / metric.Volume;
    }
}

/// <summary>
/// Readiness targets used by the gate. Defaults come from the Phase 2
/// spec's readiness checklist — operators override via DI configuration.
/// </summary>
public sealed record ReadinessTargets(
    double MaxCostPerQuestion,
    double MinCacheHitRate,
    double MaxRefusalRate,
    double MaxLatencyP95Ms,
    double MinGroundedAnswerRate)
{
    public static ReadinessTargets Default { get; } = new(
        MaxCostPerQuestion: 0.02,
        MinCacheHitRate: 0.30,
        MaxRefusalRate: 0.15,
        MaxLatencyP95Ms: 2000,
        MinGroundedAnswerRate: 0.95);
}

public sealed record ReadinessGateResult(
    bool PromotionBlocked,
    IReadOnlyList<ReadinessFailure> Failures,
    AggregatedMetric Metric,
    double LatencyP95Ms,
    double CostPerQuestion);

public sealed record ReadinessFailure(
    string Target,
    double Observed,
    double Threshold,
    string Message);
