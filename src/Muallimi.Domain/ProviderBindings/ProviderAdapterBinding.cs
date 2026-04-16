using System.Text.Json;

namespace Muallimi.Domain.ProviderBindings;

/// <summary>
/// T089 (US5) — Binding of a provider-adapter to a capability/environment/scope
/// tuple. Exactly one <c>Active</c> binding is allowed per
/// <c>(Capability, Environment, CurriculumScope)</c>; the remaining ordered
/// <see cref="FallbackChain"/> entries are binding identifiers of the same
/// capability that the ai-service fallback executor tries in order on
/// timeout / quota / provider error. Secrets are referenced via
/// <see cref="ProviderConfigurationRef"/> and never stored in the binding.
/// </summary>
public class ProviderAdapterBinding
{
    public Guid BindingId { get; set; }
    public string Capability { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string? CurriculumScope { get; set; }
    public string ProviderIdentifier { get; set; } = string.Empty;
    public string? ProviderConfigurationRef { get; set; }
    public string FallbackChain { get; set; } = "[]";
    public bool Active { get; set; }
    public bool PromotionBlockFlag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static ProviderAdapterBinding Create(
        string capability,
        string environment,
        string? curriculumScope,
        string providerIdentifier,
        IEnumerable<Guid>? fallbackChain = null,
        string? providerConfigurationRef = null)
    {
        Capabilities.ValidateOrThrow(capability);
        Environments.ValidateOrThrow(environment);
        if (string.IsNullOrWhiteSpace(providerIdentifier))
            throw new ArgumentException("provider_identifier is required.", nameof(providerIdentifier));

        var chain = (fallbackChain ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        var now = DateTime.UtcNow;
        return new ProviderAdapterBinding
        {
            BindingId = Guid.NewGuid(),
            Capability = capability,
            Environment = environment,
            CurriculumScope = NormaliseScope(curriculumScope),
            ProviderIdentifier = providerIdentifier.Trim(),
            ProviderConfigurationRef = string.IsNullOrWhiteSpace(providerConfigurationRef)
                ? null
                : providerConfigurationRef.Trim(),
            FallbackChain = JsonSerializer.Serialize(chain.Select(id => id.ToString()).ToArray()),
            Active = false,
            PromotionBlockFlag = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Activate()
    {
        if (PromotionBlockFlag)
            throw new InvalidOperationException("Binding has a promotion-block flag set.");
        Active = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        Active = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateFallbackChain(IEnumerable<Guid> chain)
    {
        var ordered = (chain ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        FallbackChain = JsonSerializer.Serialize(ordered.Select(id => id.ToString()).ToArray());
        UpdatedAt = DateTime.UtcNow;
    }

    public IReadOnlyList<Guid> ReadFallbackChain()
    {
        if (string.IsNullOrWhiteSpace(FallbackChain))
            return Array.Empty<Guid>();
        var raw = JsonSerializer.Deserialize<string[]>(FallbackChain) ?? Array.Empty<string>();
        return raw
            .Where(s => Guid.TryParse(s, out _))
            .Select(Guid.Parse)
            .ToArray();
    }

    public static string? NormaliseScope(string? scope)
    {
        if (scope is null) return null;
        var trimmed = scope.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}

public static class Capabilities
{
    public const string LlmLightweight = "llm_lightweight";
    public const string LlmStronger = "llm_stronger";
    public const string Embedding = "embedding";
    public const string Stt = "stt";
    public const string Tts = "tts";
    public const string VoiceProfile = "voice_profile";

    private static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        LlmLightweight, LlmStronger, Embedding, Stt, Tts, VoiceProfile,
    };

    public static void ValidateOrThrow(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability) || !All.Contains(capability))
            throw new ArgumentException(
                $"capability must be one of: {string.Join(", ", All)}.", nameof(capability));
    }
}

public static class Environments
{
    public const string Local = "local";
    public const string Dev = "dev";
    public const string Production = "production";

    private static readonly HashSet<string> All = new(StringComparer.Ordinal) { Local, Dev, Production };

    public static void ValidateOrThrow(string environment)
    {
        if (string.IsNullOrWhiteSpace(environment) || !All.Contains(environment))
            throw new ArgumentException(
                $"environment must be one of: {string.Join(", ", All)}.", nameof(environment));
    }
}
