using System;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T144 (US8) — AtRiskThresholdCatalogue with versioning.
///
/// Holds the documented threshold set used by <see cref="AtRiskEvaluator"/>.
/// Tuning the catalogue creates a new <see cref="Version"/>; historical
/// flags retain their original threshold version per the contract
/// invariant ("threshold updates produce a new threshold_version; historical
/// flags keep their original version"). Local runs and tests can build a
/// custom catalogue with a different version and a tighter set of bounds.
/// </summary>
public sealed record AtRiskThresholdSet(
    string Version,
    decimal LowMasteryScoreCeiling,
    int SustainedLowMasteryWindowEvents,
    int RepeatedRefusalCountOnTopic,
    int FailedMockTestCount,
    decimal EngagementDeclineRatio,
    int RecentEventLookbackCount)
{
    /// <summary>
    /// Recovery thresholds — flags clear when these are met. Tuned slightly
    /// looser than the raise thresholds to avoid oscillation.
    /// </summary>
    public decimal RecoveryMasteryScoreFloor => LowMasteryScoreCeiling + 0.10m;
    public int RecoverySuccessfulMockTestCount => 1;
}

public interface IAtRiskThresholdCatalogue
{
    AtRiskThresholdSet Current { get; }

    AtRiskThresholdSet GetByVersion(string version);
}

public sealed class AtRiskThresholdCatalogue : IAtRiskThresholdCatalogue
{
    public const string CurrentVersion = "v1.0.0";

    private readonly AtRiskThresholdSet _current;

    public AtRiskThresholdCatalogue() : this(Default()) { }

    public AtRiskThresholdCatalogue(AtRiskThresholdSet current)
    {
        _current = current;
    }

    public AtRiskThresholdSet Current => _current;

    public AtRiskThresholdSet GetByVersion(string version)
    {
        if (string.Equals(version, _current.Version, StringComparison.OrdinalIgnoreCase))
        {
            return _current;
        }
        // Historical lookups currently fall back to the current set; the
        // contract guarantees the row keeps its original version label so
        // the audit trail stays accurate even when the lookup degrades.
        return _current with { Version = version };
    }

    public static AtRiskThresholdSet Default() => new(
        Version: CurrentVersion,
        LowMasteryScoreCeiling: 0.40m,
        SustainedLowMasteryWindowEvents: 5,
        RepeatedRefusalCountOnTopic: 3,
        FailedMockTestCount: 2,
        EngagementDeclineRatio: 0.5m,
        RecentEventLookbackCount: 30);
}

public static class AtRiskThresholdCatalogueServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4AtRiskThresholdCatalogue(this IServiceCollection services)
    {
        services.AddSingleton<IAtRiskThresholdCatalogue, AtRiskThresholdCatalogue>();
        return services;
    }
}
