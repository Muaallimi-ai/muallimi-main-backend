using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.StudentExperience.Tenancy;

namespace Muallimi.Api.SchoolManagement.Tenancy;

/// <summary>
/// T013 — Phase 5 tenant filter marker.
///
/// Heavy-lifting EF Core global query filters for every Phase 5 entity
/// (SchoolTenant, SchoolAdministrator, Teacher, ClassGroup, ClassEnrolment,
/// TeacherAssignment, RosterImport, Exam, ExamQuestion, ExamAssignment,
/// ExamSubmission, LeaderboardSnapshot, Announcement, AnnouncementDelivery,
/// SchoolReport, SchoolLicense, SchoolAggregateView, Phase5DownstreamEvent)
/// live on <c>MuallimiDbContext.ApplyPhase5TenantFilters</c> so the filter
/// runs on every query by construction.
///
/// This DI extension ensures the Phase 3 tenant accessor is registered —
/// Phase 5 reuses the same <c>HttpTenantContextAccessor</c> because every
/// Phase 5 surface is served behind the same tenant header.
/// </summary>
public static class Phase5TenantQueryFilterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5Tenancy(this IServiceCollection services)
    {
        services.AddPhase3Tenancy();
        return services;
    }
}
