using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

/// <summary>
/// T034 (US1) — <see cref="SchoolTenant"/> repository.
///
/// Single read/write path for the Phase 5 school tenant aggregate. Every
/// query is scoped by <c>tenant_id</c>; cross-tenant reads are impossible
/// because callers MUST pass the ambient tenant id. Operator provisioning
/// uses <see cref="IgnoreQueryFilters"/> because the operator acts outside
/// the ambient student tenant filter.
/// </summary>
public interface ISchoolTenantRepository
{
    Task<SchoolTenant?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task<SchoolTenant?> GetByIdForOperatorAsync(Guid schoolTenantId, CancellationToken ct = default);

    Task<IReadOnlyList<SchoolTenant>> ListAsync(CancellationToken ct = default);

    Task AddAsync(SchoolTenant tenant, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class SchoolTenantRepository : ISchoolTenantRepository
{
    private readonly MuallimiDbContext _db;

    public SchoolTenantRepository(MuallimiDbContext db) => _db = db;

    public Task<SchoolTenant?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => _db.SchoolTenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SchoolTenantId == schoolTenantId, ct);

    public Task<SchoolTenant?> GetByIdForOperatorAsync(Guid schoolTenantId, CancellationToken ct = default)
        => _db.SchoolTenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.SchoolTenantId == schoolTenantId, ct);

    public async Task<IReadOnlyList<SchoolTenant>> ListAsync(CancellationToken ct = default)
        => await _db.SchoolTenants.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(SchoolTenant tenant, CancellationToken ct = default)
    {
        await _db.SchoolTenants.AddAsync(tenant, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class SchoolTenantRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolTenantRepository(this IServiceCollection services)
    {
        services.AddScoped<ISchoolTenantRepository, SchoolTenantRepository>();
        return services;
    }
}
