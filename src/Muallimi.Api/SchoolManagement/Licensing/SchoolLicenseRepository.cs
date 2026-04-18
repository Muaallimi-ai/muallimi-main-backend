using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.Licensing;

/// <summary>
/// T187 (US10) — <see cref="SchoolLicense"/> repository.
///
/// Single read/write path for the school license aggregate. Operator
/// endpoints use <see cref="IgnoreQueryFilters"/> because the operator
/// acts outside the ambient tenant filter.
/// </summary>
public interface ISchoolLicenseRepository
{
    Task<SchoolLicense?> GetBySchoolTenantIdAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task<SchoolLicense?> GetBySchoolTenantIdForOperatorAsync(Guid schoolTenantId, CancellationToken ct = default);

    Task<IReadOnlyList<SchoolLicense>> ListForOperatorAsync(CancellationToken ct = default);

    Task AddAsync(SchoolLicense license, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class SchoolLicenseRepository : ISchoolLicenseRepository
{
    private readonly MuallimiDbContext _db;

    public SchoolLicenseRepository(MuallimiDbContext db) => _db = db;

    public Task<SchoolLicense?> GetBySchoolTenantIdAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => _db.SchoolLicenses
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.TenantId == tenantId && l.SchoolTenantId == schoolTenantId, ct);

    public Task<SchoolLicense?> GetBySchoolTenantIdForOperatorAsync(Guid schoolTenantId, CancellationToken ct = default)
        => _db.SchoolLicenses
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.SchoolTenantId == schoolTenantId, ct);

    public async Task<IReadOnlyList<SchoolLicense>> ListForOperatorAsync(CancellationToken ct = default)
        => await _db.SchoolLicenses.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(SchoolLicense license, CancellationToken ct = default)
    {
        await _db.SchoolLicenses.AddAsync(license, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class SchoolLicenseRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolLicenseRepository(this IServiceCollection services)
    {
        services.AddScoped<ISchoolLicenseRepository, SchoolLicenseRepository>();
        return services;
    }
}
