using System.Text.Json;
using Muallimi.Domain.AiOperations;

namespace Muallimi.Application.AiOperations;

/// <summary>
/// T101 (US6) — Rolls a set of <see cref="AiRequestRecord"/> rows into a
/// single <see cref="AiOperationsMetric"/> row over a configurable window.
/// Pure (no DB access) so the reconciliation test (T098) can feed it a
/// deterministic sample and assert that the aggregated totals exactly
/// match the underlying records.
///
/// Shape matches
/// <c>contracts/ai-request-record-contract.md</c> and the dashboard
/// contract in <c>spec.md</c> §AI Operations:
///  - volume, refusal_rate, cache_hit_rate, grounded_answer_rate
///  - per_branch{} — count, avg_latency_ms, input_tokens, output_tokens, cost
///  - prompt_version_distribution{} — keyed by `prompt_id:version_id`
/// </summary>
public class MetricAggregator
{
    private readonly CostCalculator _costCalculator;

    public MetricAggregator(CostCalculator costCalculator)
    {
        _costCalculator = costCalculator ?? throw new ArgumentNullException(nameof(costCalculator));
    }

    public AggregatedMetric Aggregate(
        IReadOnlyList<AiRequestRecord> records,
        DateTime windowStart,
        DateTime windowEnd,
        MetricSliceFilter? filter = null)
    {
        var filtered = filter is null ? records : records.Where(r => filter.Matches(r)).ToList();

        var volume = filtered.Count;
        var refusals = filtered.Count(r => string.Equals(r.FinalOutcome, "refused", StringComparison.OrdinalIgnoreCase));
        var cacheHits = filtered.Count(r => IsCacheHit(r));
        var grounded = filtered.Count(r => IsGroundedAnswer(r));

        var perBranch = new Dictionary<string, BranchTotal>(StringComparer.Ordinal);
        foreach (var record in filtered)
        {
            var branch = _costCalculator.ResolveBranch(record.RoutingDecision, record.FinalOutcome);
            if (!perBranch.TryGetValue(branch, out var total)) total = BranchTotal.Empty;
            total = total.Add(record, _costCalculator.ComputeCost(branch, record.InputTokenCount, record.OutputTokenCount));
            perBranch[branch] = total;
        }

        var promptDistribution = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var record in filtered)
        {
            foreach (var key in ExtractPromptVersionKeys(record.PromptVersionsUsed))
            {
                promptDistribution[key] = promptDistribution.TryGetValue(key, out var c) ? c + 1 : 1;
            }
        }

        return new AggregatedMetric
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Filter = filter ?? MetricSliceFilter.Empty,
            Volume = volume,
            RefusalRate = Safe(refusals, volume),
            CacheHitRate = Safe(cacheHits, volume),
            GroundedAnswerRate = Safe(grounded, volume),
            PerBranch = perBranch
                .ToDictionary(kv => kv.Key, kv => kv.Value.ToSummary(), StringComparer.Ordinal),
            PromptVersionDistribution = promptDistribution,
        };
    }

    /// <summary>
    /// Produces a persistable <see cref="AiOperationsMetric"/> row. Callers
    /// are expected to wrap the DbContext add + save themselves so the worker
    /// can remain a thin composition.
    /// </summary>
    public AiOperationsMetric BuildRow(AggregatedMetric metric, DateTime computedAt)
    {
        return new AiOperationsMetric
        {
            MetricId = Guid.NewGuid(),
            WindowStart = metric.WindowStart.ToString("O"),
            WindowEnd = metric.WindowEnd.ToString("O"),
            CurriculumType = metric.Filter.CurriculumType ?? string.Empty,
            Grade = metric.Filter.Grade ?? string.Empty,
            Subject = metric.Filter.Subject ?? string.Empty,
            TutorLanguage = metric.Filter.TutorLanguage ?? string.Empty,
            SessionMode = metric.Filter.SessionMode ?? string.Empty,
            Volume = metric.Volume,
            RefusalRate = metric.RefusalRate,
            CacheHitRate = metric.CacheHitRate,
            GroundedAnswerRate = metric.GroundedAnswerRate,
            PerBranch = JsonSerializer.Serialize(metric.PerBranch),
            PromptVersionDistribution = JsonSerializer.Serialize(metric.PromptVersionDistribution),
            ComputedAt = computedAt,
        };
    }

    private static bool IsCacheHit(AiRequestRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.RoutingDecision)) return false;
        try
        {
            using var doc = JsonDocument.Parse(record.RoutingDecision);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (doc.RootElement.TryGetProperty("chosen_source", out var src) &&
                src.ValueKind == JsonValueKind.String)
            {
                return string.Equals(src.GetString(), CostCalculator.BranchCache, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static bool IsGroundedAnswer(AiRequestRecord record)
    {
        if (!string.Equals(record.FinalOutcome, "answered", StringComparison.OrdinalIgnoreCase))
            return false;
        // An answered request is grounded when it wasn't a lesson-redirect
        // fallback and evidence was resolved (represented as a non-empty
        // stages array containing a retrieval stage with outcome=passed).
        if (string.IsNullOrWhiteSpace(record.Stages)) return true; // answered implies ground path
        try
        {
            using var doc = JsonDocument.Parse(record.Stages);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return true;
            foreach (var stage in doc.RootElement.EnumerateArray())
            {
                if (stage.ValueKind != JsonValueKind.Object) continue;
                if (stage.TryGetProperty("stage", out var name) && name.ValueKind == JsonValueKind.String &&
                    string.Equals(name.GetString(), "grounding", StringComparison.OrdinalIgnoreCase))
                {
                    if (stage.TryGetProperty("decision", out var decision) &&
                        decision.ValueKind == JsonValueKind.String)
                    {
                        return string.Equals(decision.GetString(), "passed", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
        catch (JsonException) { }
        return true;
    }

    private static IEnumerable<string> ExtractPromptVersionKeys(string promptVersionsUsedJson)
    {
        if (string.IsNullOrWhiteSpace(promptVersionsUsedJson)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(promptVersionsUsedJson); }
        catch (JsonException) { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var key = KeyFromObject(item);
                    if (key is not null) yield return key;
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    var key = KeyFromObject(property.Value);
                    if (key is not null) yield return key;
                }
            }
        }
    }

    private static string? KeyFromObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? promptId = null;
        string? versionId = null;
        if (element.TryGetProperty("prompt_id", out var p) && p.ValueKind == JsonValueKind.String)
            promptId = p.GetString();
        if (element.TryGetProperty("version_id", out var v) && v.ValueKind == JsonValueKind.String)
            versionId = v.GetString();
        if (string.IsNullOrWhiteSpace(promptId) || string.IsNullOrWhiteSpace(versionId)) return null;
        return $"{promptId}:{versionId}";
    }

    private static double Safe(long numerator, long denominator)
        => denominator == 0 ? 0d : (double)numerator / denominator;

    private readonly record struct BranchTotal(long Count, long InputTokens, long OutputTokens, long LatencyMsSum, double CostTotal)
    {
        public static BranchTotal Empty => new(0, 0, 0, 0, 0d);

        public BranchTotal Add(AiRequestRecord record, double cost) => new(
            Count + 1,
            InputTokens + record.InputTokenCount,
            OutputTokens + record.OutputTokenCount,
            LatencyMsSum + record.LatencyMs,
            CostTotal + cost);

        public BranchSummary ToSummary() => new(
            Count: Count,
            InputTokens: InputTokens,
            OutputTokens: OutputTokens,
            AvgLatencyMs: Count == 0 ? 0 : (double)LatencyMsSum / Count,
            CostTotal: Math.Round(CostTotal, 6));
    }
}

public sealed record MetricSliceFilter(
    string? CurriculumType = null,
    string? Grade = null,
    string? Subject = null,
    string? TutorLanguage = null,
    string? SessionMode = null)
{
    public static MetricSliceFilter Empty { get; } = new();

    public bool Matches(AiRequestRecord record)
    {
        if (CurriculumType is not null && !string.Equals(CurriculumType, record.CurriculumType, StringComparison.OrdinalIgnoreCase)) return false;
        if (Grade is not null && !string.Equals(Grade, record.Grade, StringComparison.OrdinalIgnoreCase)) return false;
        if (Subject is not null && !string.Equals(Subject, record.Subject, StringComparison.OrdinalIgnoreCase)) return false;
        if (TutorLanguage is not null && !string.Equals(TutorLanguage, record.TutorLanguage, StringComparison.OrdinalIgnoreCase)) return false;
        if (SessionMode is not null && !string.Equals(SessionMode, record.SessionMode, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

public sealed class AggregatedMetric
{
    public required DateTime WindowStart { get; init; }
    public required DateTime WindowEnd { get; init; }
    public required MetricSliceFilter Filter { get; init; }
    public required long Volume { get; init; }
    public required double RefusalRate { get; init; }
    public required double CacheHitRate { get; init; }
    public required double GroundedAnswerRate { get; init; }
    public required IReadOnlyDictionary<string, BranchSummary> PerBranch { get; init; }
    public required IReadOnlyDictionary<string, long> PromptVersionDistribution { get; init; }
}

public sealed record BranchSummary(
    long Count,
    long InputTokens,
    long OutputTokens,
    double AvgLatencyMs,
    double CostTotal);
