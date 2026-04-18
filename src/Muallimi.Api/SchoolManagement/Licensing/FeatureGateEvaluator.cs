using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.SchoolManagement.Licensing;

/// <summary>
/// T186 (US10) — feature-gate evaluator.
///
/// Reads the <c>SchoolLicense.FeatureGates</c> JSON and returns whether a
/// named feature is enabled for a school. Endpoints that sit behind a
/// feature gate call <see cref="IsFeatureEnabledAsync"/> and refuse with
/// 403 / <c>feature_gated</c> when it returns false.
///
/// Gate JSON shape: <c>{"exams":true,"announcements":false,"reports":true}</c>
/// Missing key ⇒ treat as enabled (fail-open for unknown features — operator
/// must explicitly disable).
/// </summary>
public interface IFeatureGateEvaluator
{
    Task<bool> IsFeatureEnabledAsync(Guid schoolTenantId, string featureKey, CancellationToken ct = default);

    bool IsFeatureEnabledInJson(string featureGatesJson, string featureKey);
}

public sealed class FeatureGateEvaluator : IFeatureGateEvaluator
{
    private readonly ISchoolLicenseRepository _repo;

    public FeatureGateEvaluator(ISchoolLicenseRepository repo) => _repo = repo;

    public async Task<bool> IsFeatureEnabledAsync(
        Guid schoolTenantId,
        string featureKey,
        CancellationToken ct = default)
    {
        var license = await _repo.GetBySchoolTenantIdForOperatorAsync(schoolTenantId, ct);
        if (license is null) return false;
        return IsFeatureEnabledInJson(license.FeatureGates, featureKey);
    }

    public bool IsFeatureEnabledInJson(string featureGatesJson, string featureKey)
    {
        if (string.IsNullOrWhiteSpace(featureGatesJson) || string.IsNullOrWhiteSpace(featureKey))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(featureGatesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;
            if (!doc.RootElement.TryGetProperty(featureKey, out var prop)) return true;
            return prop.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => true,
            };
        }
        catch (JsonException)
        {
            return true;
        }
    }
}

public static class FeatureGateEvaluatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5FeatureGateEvaluator(this IServiceCollection services)
    {
        services.AddScoped<IFeatureGateEvaluator, FeatureGateEvaluator>();
        return services;
    }
}
