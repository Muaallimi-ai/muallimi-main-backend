using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Application.AiOperations;
using Muallimi.Application.Audit;
using Muallimi.Infrastructure.AiOperations;

namespace Muallimi.Api.AiOperations;

/// <summary>
/// T068 (US3) — Admin surface for the AI tutor's routing thresholds. Every
/// mutation is validated, persisted via <see cref="IRoutingConfigurationStore"/>,
/// audited, and published as a `routing.config.updated` event so the
/// ai-service <c>RoutingConfigCache</c> invalidates at runtime (SC-009).
/// Role enforcement (operator / curriculum lead) is expected to be applied
/// by the AiOperationsAuthorizationFilter wired in Phase 2 foundational.
/// </summary>
public static class RoutingConfigurationEndpoint
{
    public const string BaseRoute = "/internal/ai-ops/routing-configuration";

    public static void MapRoutingConfigurationEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(BaseRoute, (IRoutingConfigurationStore store) =>
        {
            var current = store.Get();
            return Results.Ok(Map(current));
        })
        .WithName("GetRoutingConfiguration")
        .WithTags("AiOperations");

        routes.MapPut(BaseRoute, async (
            HttpContext http,
            RoutingConfigurationUpdateRequest request,
            IRoutingConfigurationStore store,
            IRoutingConfigUpdatedPublisher publisher,
            AuditEventEmitter audit,
            CancellationToken ct) =>
        {
            if (request is null)
                return Results.BadRequest(new { error = "request body is required." });

            var next = new RoutingConfiguration(
                CacheMatchThreshold: request.CacheMatchThreshold,
                HighConfidenceThreshold: request.HighConfidenceThreshold,
                MinGroundingThreshold: request.MinGroundingThreshold,
                StrongerTierComplexityThreshold: request.StrongerTierComplexityThreshold);

            try
            {
                next.Validate();
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var correlationId = http.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString("N");
            var actor = http.Items["ActorRole"]?.ToString() ?? http.Request.Headers["X-Actor-Id"].FirstOrDefault() ?? "anonymous";
            var tenantId = http.Items["TenantId"]?.ToString() ?? http.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "local";
            var previous = store.Get();

            store.Set(next);

            audit.Emit(new AuditEvent
            {
                EventCategory = "provider-configuration",
                Action = "routing-configuration-updated",
                TargetType = "RoutingConfiguration",
                TargetId = "ai-tutor",
                ActorId = actor,
                TenantId = tenantId,
                Outcome = "succeeded",
                CorrelationId = correlationId,
                Reason = RoutingConfigurationDiff.Describe(previous, next),
            });

            await publisher.PublishAsync(new RoutingConfigurationUpdatedEvent(
                EventId: Guid.NewGuid().ToString("N"),
                CorrelationId: correlationId,
                ActorId: actor,
                TenantId: tenantId,
                OccurredAt: DateTime.UtcNow,
                Configuration: next), ct);

            return Results.Ok(Map(next));
        })
        .WithName("UpdateRoutingConfiguration")
        .WithTags("AiOperations");
    }

    public static void AddRoutingConfigurationServices(this IServiceCollection services)
    {
        services.AddSingleton<IRoutingConfigurationStore, InMemoryRoutingConfigurationStore>();
        services.AddSingleton<InMemoryRoutingConfigUpdatedPublisher>();
        services.AddSingleton<IRoutingConfigUpdatedPublisher>(
            sp => sp.GetRequiredService<InMemoryRoutingConfigUpdatedPublisher>());
    }

    private static RoutingConfigurationResponse Map(RoutingConfiguration c) => new(
        cache_match_threshold: c.CacheMatchThreshold,
        high_confidence_threshold: c.HighConfidenceThreshold,
        min_grounding_threshold: c.MinGroundingThreshold,
        stronger_tier_complexity_threshold: c.StrongerTierComplexityThreshold);
}

public record RoutingConfigurationUpdateRequest(
    double CacheMatchThreshold,
    double HighConfidenceThreshold,
    double MinGroundingThreshold,
    double StrongerTierComplexityThreshold);

public record RoutingConfigurationResponse(
    double cache_match_threshold,
    double high_confidence_threshold,
    double min_grounding_threshold,
    double stronger_tier_complexity_threshold);

internal static class RoutingConfigurationDiff
{
    public static string Describe(RoutingConfiguration previous, RoutingConfiguration next)
    {
        var changes = new List<string>();
        if (!Eq(previous.CacheMatchThreshold, next.CacheMatchThreshold))
            changes.Add($"cache_match_threshold:{previous.CacheMatchThreshold:F3}->{next.CacheMatchThreshold:F3}");
        if (!Eq(previous.HighConfidenceThreshold, next.HighConfidenceThreshold))
            changes.Add($"high_confidence_threshold:{previous.HighConfidenceThreshold:F3}->{next.HighConfidenceThreshold:F3}");
        if (!Eq(previous.MinGroundingThreshold, next.MinGroundingThreshold))
            changes.Add($"min_grounding_threshold:{previous.MinGroundingThreshold:F3}->{next.MinGroundingThreshold:F3}");
        if (!Eq(previous.StrongerTierComplexityThreshold, next.StrongerTierComplexityThreshold))
            changes.Add($"stronger_tier_complexity_threshold:{previous.StrongerTierComplexityThreshold:F3}->{next.StrongerTierComplexityThreshold:F3}");
        return changes.Count == 0 ? "no-op" : string.Join(", ", changes);
    }

    private static bool Eq(double a, double b) => Math.Abs(a - b) < 1e-9;
}
