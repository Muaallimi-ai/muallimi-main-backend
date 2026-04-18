using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.ParentDashboard;

/// <summary>
/// T070 (US2) — <see cref="ParentProfile"/> repository.
///
/// Resolves the authenticated parent's profile inside the active tenant.
/// The dashboard endpoint needs the parent's locale + timezone so mastery
/// deltas and streak surfaces render in the right calendar. Queries bypass
/// the global tenant filter and pin <c>tenant_id</c> explicitly so a
/// misconfigured ambient tenant cannot silently leak another family's row.
/// </summary>
public interface IParentProfileRepository
{
    Task<ParentProfile?> GetAsync(
        Guid tenantId,
        Guid parentProfileId,
        CancellationToken ct = default);

    Task<ParentProfile?> GetByIdentityAsync(
        Guid tenantId,
        Guid identityId,
        CancellationToken ct = default);
}

public sealed class ParentProfileRepository : IParentProfileRepository
{
    private readonly MuallimiDbContext _db;

    public ParentProfileRepository(MuallimiDbContext db) => _db = db;

    public Task<ParentProfile?> GetAsync(
        Guid tenantId,
        Guid parentProfileId,
        CancellationToken ct = default)
        => _db.ParentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId && p.ParentProfileId == parentProfileId,
                ct);

    public Task<ParentProfile?> GetByIdentityAsync(
        Guid tenantId,
        Guid identityId,
        CancellationToken ct = default)
        => _db.ParentProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId && p.IdentityId == identityId,
                ct);
}

public static class ParentProfileRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ParentProfileRepository(this IServiceCollection services)
    {
        services.AddScoped<IParentProfileRepository, ParentProfileRepository>();
        return services;
    }
}
