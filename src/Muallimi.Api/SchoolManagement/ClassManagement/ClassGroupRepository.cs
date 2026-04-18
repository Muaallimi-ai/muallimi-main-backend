using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.ClassManagement;

/// <summary>
/// T071 (US3) — <see cref="ClassGroup"/> repository.
///
/// Single read/write path for the class-group aggregate. Every lookup is
/// tenant + school-tenant scoped so cross-school reads are impossible.
/// </summary>
public interface IClassGroupRepository
{
    Task AddAsync(ClassGroup row, CancellationToken ct = default);

    Task<ClassGroup?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid classGroupId, CancellationToken ct = default);

    Task<IReadOnlyList<ClassGroup>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class ClassGroupRepository : IClassGroupRepository
{
    private readonly MuallimiDbContext _db;

    public ClassGroupRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(ClassGroup row, CancellationToken ct = default)
    {
        _db.ClassGroups.Add(row);
        return Task.CompletedTask;
    }

    public Task<ClassGroup?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid classGroupId, CancellationToken ct = default)
        => _db.ClassGroups
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c =>
                c.TenantId == tenantId
                && c.SchoolTenantId == schoolTenantId
                && c.ClassGroupId == classGroupId,
                ct);

    public async Task<IReadOnlyList<ClassGroup>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => await _db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.SchoolTenantId == schoolTenantId)
            .OrderBy(c => c.Grade)
            .ThenBy(c => c.SectionLabel)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class ClassGroupRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ClassGroupRepository(this IServiceCollection services)
    {
        services.AddScoped<IClassGroupRepository, ClassGroupRepository>();
        return services;
    }
}
