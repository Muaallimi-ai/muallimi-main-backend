using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.AdminOnboarding;

/// <summary>
/// T035 (US1) — <see cref="SchoolAdministrator"/> repository.
///
/// Reads / writes school administrator role-bindings. Invitation tokens
/// are stored on the administrator row (opaque Guid) and consumed by
/// <see cref="IAdminOnboardingService"/>. Lookup by invitation token
/// bypasses the tenant filter because the invited admin is unauthenticated
/// during onboarding.
/// </summary>
public interface ISchoolAdminRepository
{
    Task<SchoolAdministrator?> GetByIdAsync(Guid tenantId, Guid schoolAdminId, CancellationToken ct = default);

    Task<SchoolAdministrator?> GetByInvitationTokenAsync(Guid invitationToken, CancellationToken ct = default);

    Task<SchoolAdministrator?> GetByUserIdentityAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid userIdentityId,
        CancellationToken ct = default);

    Task<IReadOnlyList<SchoolAdministrator>> ListForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default);

    Task AddAsync(SchoolAdministrator admin, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class SchoolAdminRepository : ISchoolAdminRepository
{
    private readonly MuallimiDbContext _db;

    public SchoolAdminRepository(MuallimiDbContext db) => _db = db;

    public Task<SchoolAdministrator?> GetByIdAsync(Guid tenantId, Guid schoolAdminId, CancellationToken ct = default)
        => _db.SchoolAdministrators
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.SchoolAdminId == schoolAdminId, ct);

    public Task<SchoolAdministrator?> GetByInvitationTokenAsync(Guid invitationToken, CancellationToken ct = default)
        => _db.SchoolAdministrators
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.SchoolAdminId == invitationToken, ct);

    public Task<SchoolAdministrator?> GetByUserIdentityAsync(
        Guid tenantId,
        Guid schoolTenantId,
        Guid userIdentityId,
        CancellationToken ct = default)
        => _db.SchoolAdministrators
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                a => a.TenantId == tenantId
                     && a.SchoolTenantId == schoolTenantId
                     && a.UserIdentityId == userIdentityId,
                ct);

    public async Task<IReadOnlyList<SchoolAdministrator>> ListForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default)
        => await _db.SchoolAdministrators
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.SchoolTenantId == schoolTenantId)
            .ToListAsync(ct);

    public async Task AddAsync(SchoolAdministrator admin, CancellationToken ct = default)
    {
        await _db.SchoolAdministrators.AddAsync(admin, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class SchoolAdminRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolAdminRepository(this IServiceCollection services)
    {
        services.AddScoped<ISchoolAdminRepository, SchoolAdminRepository>();
        return services;
    }
}
