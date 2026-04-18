using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.Tenancy;

namespace Muallimi.Api.Engagement.Tenancy;

/// <summary>
/// T009 — Phase 4 tenant filter marker.
///
/// The heavy-lifting EF Core global query filters for every Phase 4 entity
/// (ProgressRecord, MasteryState, StreakState, BadgeAward, FocusArea,
/// GuardrailDecisionTrail, WeeklyReport, AtRiskFlag, InterventionPrompt,
/// Phase4DownstreamEvent, ParentProfile, ChildLink, ParentNotification,
/// OperatorImpersonationAudit) live on <c>MuallimiDbContext.ApplyPhase4TenantFilters</c>
/// so the filter runs on every query by construction.
///
/// This DI extension just ensures the Phase 3 tenant accessor is registered —
/// Phase 4 reuses the same <c>HttpTenantContextAccessor</c> because every
/// Phase 4 surface is served behind the same tenant header.
/// </summary>
public static class Phase4TenantQueryFilterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4Tenancy(this IServiceCollection services)
    {
        services.AddPhase3Tenancy();
        return services;
    }
}
