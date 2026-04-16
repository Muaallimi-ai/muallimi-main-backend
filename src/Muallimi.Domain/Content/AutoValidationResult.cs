using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Content;

/// <summary>
/// Records the outcome of Tier 1 auto-validation for a GeneratedAsset.
/// Any failed blocking check sets decision = Failed and prevents human review entry.
/// </summary>
public class AutoValidationResult
{
    public Guid ResultId { get; private set; }
    public Guid AssetId { get; private set; }

    /// <summary>JSON map of check name to passed/failed with detail string.</summary>
    public string Checks { get; private set; } = "{}";

    /// <summary>JSON array of source chunk references that ground the asset.</summary>
    public string GroundingEvidence { get; private set; } = "[]";

    /// <summary>JSON object with MSA grammar, vocabulary, and diacritic check outcomes.</summary>
    public string? ArabicQuality { get; private set; }

    /// <summary>JSON object with duration, schema, headless-render, and file integrity results.</summary>
    public string? Rendering { get; private set; }

    /// <summary>JSON object with alignment tolerance measurement.</summary>
    public string? NarrationSync { get; private set; }

    /// <summary>JSON object with transcript and subtitle presence.</summary>
    public string? Accessibility { get; private set; }

    /// <summary>JSON object with metadata-vs-source comparison result.</summary>
    public string? Alignment { get; private set; }

    public AutoValidationDecision Decision { get; private set; }
    public DateTime ValidatedAt { get; private set; }

    private AutoValidationResult() { }

    public static AutoValidationResult Create(
        Guid assetId,
        string checks,
        string groundingEvidence,
        string? arabicQuality,
        string? rendering,
        string? narrationSync,
        string? accessibility,
        string? alignment,
        AutoValidationDecision decision)
    {
        return new AutoValidationResult
        {
            ResultId = Guid.NewGuid(),
            AssetId = assetId,
            Checks = checks,
            GroundingEvidence = groundingEvidence,
            ArabicQuality = arabicQuality,
            Rendering = rendering,
            NarrationSync = narrationSync,
            Accessibility = accessibility,
            Alignment = alignment,
            Decision = decision,
            ValidatedAt = DateTime.UtcNow
        };
    }

    public bool Passed => Decision == AutoValidationDecision.Passed;
}
