using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Infrastructure.Persistence;
using RosterImportEntity = Muallimi.Domain.SchoolManagement.RosterImport;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T053 (US2) — <see cref="RosterImport"/> repository.
///
/// Single read/write path for the roster-import aggregate. Every lookup
/// is tenant + school-tenant scoped so cross-school reads are impossible.
/// </summary>
public interface IRosterImportRepository
{
    Task<Guid> AddAsync(RosterImportEntity row, CancellationToken ct = default);

    Task<RosterImportEntity?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid rosterImportId, CancellationToken ct = default);

    Task<IReadOnlyList<RosterImportEntity>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class RosterImportRepository : IRosterImportRepository
{
    private readonly MuallimiDbContext _db;

    public RosterImportRepository(MuallimiDbContext db) => _db = db;

    public Task<Guid> AddAsync(RosterImportEntity row, CancellationToken ct = default)
    {
        _db.RosterImports.Add(row);
        return Task.FromResult(row.RosterImportId);
    }

    public Task<RosterImportEntity?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid rosterImportId, CancellationToken ct = default)
        => _db.RosterImports
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId
                && r.SchoolTenantId == schoolTenantId
                && r.RosterImportId == rosterImportId,
                ct);

    public async Task<IReadOnlyList<RosterImportEntity>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => await _db.RosterImports
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.SchoolTenantId == schoolTenantId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class RosterImportRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5RosterImportRepository(this IServiceCollection services)
    {
        services.AddScoped<IRosterImportRepository, RosterImportRepository>();
        return services;
    }
}
