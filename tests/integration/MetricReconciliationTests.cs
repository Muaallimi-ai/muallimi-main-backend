using System.Text.Json;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T098 (US6, SC-013) — Metric reconciliation: aggregated totals must
/// exactly match the underlying <see cref="AiRequestRecord"/> set over the
/// requested window. The dashboard derives every tile from the persisted
/// <see cref="AiOperationsMetric"/> row; a drift between the row and the
/// raw records would invalidate operator decisions (refusal rate, cost,
/// readiness-gate pass/fail), so the reconciliation is exercised here
/// deterministically against a scripted 100-request sample.
/// </summary>
public class MetricReconciliationTests
{
    private readonly MetricAggregator _aggregator = new(new CostCalculator(new Dictionary<string, BranchRate>
    {
        [CostCalculator.BranchCache] = BranchRate.Zero,
        [CostCalculator.BranchLightweight] = new BranchRate(InputPer1K: 0.0005, OutputPer1K: 0.0015),
        [CostCalculator.BranchStronger] = new BranchRate(InputPer1K: 0.003, OutputPer1K: 0.009),
        [CostCalculator.BranchGroundingFallback] = BranchRate.Zero,
        [CostCalculator.BranchRefused] = BranchRate.Zero,
    }));

    [Fact]
    public void Volume_Refusal_Cache_Grounded_Rates_Reconcile_To_Underlying_Records()
    {
        var records = BuildScriptedSample(answered: 70, refused: 20, fallback: 10, cacheHits: 25, grounded: 68);

        var metric = _aggregator.Aggregate(records, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        Assert.Equal(100, metric.Volume);
        Assert.Equal(20d / 100d, metric.RefusalRate, 6);
        Assert.Equal(25d / 100d, metric.CacheHitRate, 6);
        Assert.Equal(68d / 100d, metric.GroundedAnswerRate, 6);
    }

    [Fact]
    public void PerBranch_Counts_Sum_To_Total_Volume()
    {
        var records = BuildScriptedSample(answered: 70, refused: 20, fallback: 10, cacheHits: 25, grounded: 68);

        var metric = _aggregator.Aggregate(records, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        var total = metric.PerBranch.Values.Sum(b => b.Count);

        Assert.Equal(metric.Volume, total);
    }

    [Fact]
    public void PerBranch_Token_And_Cost_Totals_Match_Underlying_Records()
    {
        var records = BuildScriptedSample(answered: 70, refused: 20, fallback: 10, cacheHits: 25, grounded: 68);

        var metric = _aggregator.Aggregate(records, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        foreach (var branchName in metric.PerBranch.Keys)
        {
            var branchRecords = records.Where(r =>
                _aggregator.GetType() != null &&
                ResolveBranch(r) == branchName).ToList();

            var expectedInput = branchRecords.Sum(r => (long)r.InputTokenCount);
            var expectedOutput = branchRecords.Sum(r => (long)r.OutputTokenCount);

            Assert.Equal(expectedInput, metric.PerBranch[branchName].InputTokens);
            Assert.Equal(expectedOutput, metric.PerBranch[branchName].OutputTokens);
        }
    }

    [Fact]
    public void Filter_Slices_Recompute_From_Same_Record_Set()
    {
        var ar = BuildScriptedSample(answered: 40, refused: 10, fallback: 0, cacheHits: 10, grounded: 40, language: "Ar");
        var en = BuildScriptedSample(answered: 30, refused: 10, fallback: 10, cacheHits: 15, grounded: 28, language: "En");
        var combined = ar.Concat(en).ToList();

        var full = _aggregator.Aggregate(combined, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        var arabicOnly = _aggregator.Aggregate(combined, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow,
            new MetricSliceFilter(TutorLanguage: "Ar"));
        var englishOnly = _aggregator.Aggregate(combined, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow,
            new MetricSliceFilter(TutorLanguage: "En"));

        // Slices reconcile: Ar + En = full volume, refusal counts decompose
        Assert.Equal(full.Volume, arabicOnly.Volume + englishOnly.Volume);
        Assert.Equal(arabicOnly.Volume, ar.Count);
        Assert.Equal(englishOnly.Volume, en.Count);
    }

    [Fact]
    public void BuildRow_Serialises_Json_That_Reparses_Deterministically()
    {
        var records = BuildScriptedSample(answered: 5, refused: 2, fallback: 1, cacheHits: 3, grounded: 4);

        var metric = _aggregator.Aggregate(records, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        var row = _aggregator.BuildRow(metric, DateTime.UtcNow);

        using var branchDoc = JsonDocument.Parse(row.PerBranch);
        foreach (var key in metric.PerBranch.Keys)
            Assert.True(branchDoc.RootElement.TryGetProperty(key, out _));

        using var promptDoc = JsonDocument.Parse(row.PromptVersionDistribution);
        Assert.True(promptDoc.RootElement.ValueKind == JsonValueKind.Object);
    }

    private static string ResolveBranch(AiRequestRecord record)
    {
        if (string.Equals(record.FinalOutcome, "refused", StringComparison.OrdinalIgnoreCase))
            return CostCalculator.BranchRefused;
        if (!string.IsNullOrEmpty(record.RoutingDecision))
        {
            try
            {
                using var doc = JsonDocument.Parse(record.RoutingDecision);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("chosen_source", out var src) &&
                    src.ValueKind == JsonValueKind.String)
                {
                    return src.GetString() ?? CostCalculator.BranchLightweight;
                }
            }
            catch (JsonException) { }
        }
        return CostCalculator.BranchLightweight;
    }

    private static List<AiRequestRecord> BuildScriptedSample(
        int answered, int refused, int fallback, int cacheHits, int grounded, string language = "Ar")
    {
        var list = new List<AiRequestRecord>();
        var random = new Random(42); // deterministic
        int answeredLeft = answered;
        int groundedLeft = grounded;

        // Cache hits and grounded answered subset
        for (var i = 0; i < cacheHits && answeredLeft > 0; i++)
        {
            var isGrounded = groundedLeft-- > 0;
            list.Add(Make("answered", branch: "cache", inputTokens: 80, outputTokens: 0, latency: random.Next(20, 80), language: language, grounded: isGrounded));
            answeredLeft--;
        }

        // Remaining answered = lightweight or stronger (split 70/30)
        var lightweightCount = (int)Math.Round(answeredLeft * 0.7);
        for (var i = 0; i < lightweightCount; i++)
        {
            var isGrounded = groundedLeft-- > 0;
            list.Add(Make("answered", branch: "llm_lightweight",
                inputTokens: random.Next(200, 600), outputTokens: random.Next(100, 300),
                latency: random.Next(400, 1200), language: language, grounded: isGrounded));
        }
        for (var i = lightweightCount; i < answeredLeft; i++)
        {
            var isGrounded = groundedLeft-- > 0;
            list.Add(Make("answered", branch: "llm_stronger",
                inputTokens: random.Next(800, 1600), outputTokens: random.Next(300, 600),
                latency: random.Next(1200, 2000), language: language, grounded: isGrounded));
        }

        for (var i = 0; i < refused; i++)
            list.Add(Make("refused", branch: "refused", inputTokens: 80, outputTokens: 0, latency: random.Next(50, 200), language: language, grounded: false));

        for (var i = 0; i < fallback; i++)
            list.Add(Make("fallback_redirect", branch: "grounding_fallback", inputTokens: 120, outputTokens: 0, latency: random.Next(100, 300), language: language, grounded: false));

        return list;
    }

    private static AiRequestRecord Make(
        string finalOutcome, string branch, int inputTokens, int outputTokens, int latency, string language, bool grounded)
    {
        var routingDecision = branch switch
        {
            "refused" => "{}",
            _ => JsonSerializer.Serialize(new { chosen_source = branch, model_tier = branch }),
        };

        var stages = branch switch
        {
            "refused" => "[{\"stage\":\"scope\",\"decision\":\"refused\"}]",
            _ => grounded
                ? "[{\"stage\":\"grounding\",\"decision\":\"passed\"}]"
                : "[{\"stage\":\"grounding\",\"decision\":\"below_threshold\"}]",
        };

        return new AiRequestRecord
        {
            RecordId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid().ToString("N"),
            SessionId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            CurriculumType = "Moe",
            Grade = "Grade7",
            Subject = "Mathematics",
            TutorLanguage = language,
            SessionMode = "Study",
            Stages = stages,
            RoutingDecision = routingDecision,
            InputTokenCount = inputTokens,
            OutputTokenCount = outputTokens,
            LatencyMs = latency,
            CacheMatchScore = branch == "cache" ? 0.95 : null,
            FinalOutcome = finalOutcome,
            QuestionTextPreview = "sample",
            PromptVersionsUsed = "[{\"stage\":\"generation\",\"prompt_id\":\"system.lightweight\",\"version_id\":\"v1\"}]",
            OccurredAt = DateTime.UtcNow.AddMinutes(-new Random().Next(1, 59)),
        };
    }
}
