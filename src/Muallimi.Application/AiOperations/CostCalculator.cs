using System.Text.Json;

namespace Muallimi.Application.AiOperations;

/// <summary>
/// T103 (US6) — Translates per-record input/output token counts into a
/// per-branch cost total. Rates are expressed in cost-units-per-1K-tokens
/// and keyed by model tier (matching the routing decision's
/// `chosen_source` / `model_tier`). Local-mode rates default to zero —
/// callers can override via configuration in production environments so
/// the dashboard surfaces a meaningful cost-per-question number.
/// </summary>
public class CostCalculator
{
    public const string BranchCache = "cache";
    public const string BranchLightweight = "llm_lightweight";
    public const string BranchStronger = "llm_stronger";
    public const string BranchGroundingFallback = "grounding_fallback";
    public const string BranchRefused = "refused";

    private readonly IReadOnlyDictionary<string, BranchRate> _rates;

    public CostCalculator(IReadOnlyDictionary<string, BranchRate>? rates = null)
    {
        _rates = rates ?? DefaultRates();
    }

    /// <summary>
    /// Compute the branch of a single <c>AiRequestRecord</c> based on its
    /// persisted <c>routing_decision</c> JSON (or final outcome when no
    /// routing decision was reached, e.g. pre-generation refusals).
    /// </summary>
    public string ResolveBranch(string routingDecisionJson, string finalOutcome)
    {
        if (string.Equals(finalOutcome, "refused", StringComparison.OrdinalIgnoreCase))
            return BranchRefused;

        if (string.IsNullOrWhiteSpace(routingDecisionJson))
            return string.Equals(finalOutcome, "fallback_redirect", StringComparison.OrdinalIgnoreCase)
                ? BranchGroundingFallback
                : BranchLightweight;

        try
        {
            using var doc = JsonDocument.Parse(routingDecisionJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return BranchLightweight;

            if (doc.RootElement.TryGetProperty("chosen_source", out var chosen) &&
                chosen.ValueKind == JsonValueKind.String)
            {
                var value = chosen.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }

            if (doc.RootElement.TryGetProperty("model_tier", out var tier) &&
                tier.ValueKind == JsonValueKind.String)
            {
                var value = tier.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value!;
            }
        }
        catch (JsonException)
        {
            // Fall through
        }

        return BranchLightweight;
    }

    /// <summary>
    /// Compute the cost contribution of a single request on a given branch.
    /// Refused/cache/grounding_fallback branches consume zero live generation
    /// tokens; input tokens are still billed for cache lookups and guardrail
    /// stages that call the classifier model.
    /// </summary>
    public double ComputeCost(string branch, int inputTokenCount, int outputTokenCount)
    {
        if (!_rates.TryGetValue(branch, out var rate)) rate = BranchRate.Zero;
        return (inputTokenCount / 1000.0 * rate.InputPer1K)
             + (outputTokenCount / 1000.0 * rate.OutputPer1K);
    }

    public BranchRate GetRate(string branch)
        => _rates.TryGetValue(branch, out var rate) ? rate : BranchRate.Zero;

    public IReadOnlyDictionary<string, BranchRate> AllRates => _rates;

    /// <summary>
    /// Local defaults are zero so the local parity walkthrough does not
    /// invent a cost figure. Production bindings override these via DI.
    /// </summary>
    public static IReadOnlyDictionary<string, BranchRate> DefaultRates() => new Dictionary<string, BranchRate>
    {
        [BranchCache] = BranchRate.Zero,
        [BranchLightweight] = BranchRate.Zero,
        [BranchStronger] = BranchRate.Zero,
        [BranchGroundingFallback] = BranchRate.Zero,
        [BranchRefused] = BranchRate.Zero,
    };
}

public readonly record struct BranchRate(double InputPer1K, double OutputPer1K)
{
    public static BranchRate Zero => new(0.0, 0.0);
}
