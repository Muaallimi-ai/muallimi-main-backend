using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolReports.ReportAggregation;

/// <summary>
/// T172 (US9) — repository for <see cref="SchoolReport"/>. Every read path
/// is tenant + school-tenant scoped so reports cannot leak across schools.
/// Writes defer <c>SaveChangesAsync</c> to the caller so the Phase 5 outbox
/// row can flush in the same transaction as the report status transition.
/// </summary>
public interface ISchoolReportRepository
{
    Task AddAsync(SchoolReport row, CancellationToken ct = default);

    Task<SchoolReport?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid schoolReportId, CancellationToken ct = default);

    Task<IReadOnlyList<SchoolReport>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task<IReadOnlyList<SchoolReport>> ListPendingAsync(int batchSize, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class SchoolReportRepository : ISchoolReportRepository
{
    private readonly MuallimiDbContext _db;

    public SchoolReportRepository(MuallimiDbContext db) => _db = db;

    public Task AddAsync(SchoolReport row, CancellationToken ct = default)
    {
        if (row.SchoolReportId == Guid.Empty)
        {
            row.SchoolReportId = Guid.NewGuid();
        }
        if (row.CreatedAt == default)
        {
            row.CreatedAt = DateTime.UtcNow;
        }
        _db.SchoolReports.Add(row);
        return Task.CompletedTask;
    }

    public Task<SchoolReport?> GetByIdAsync(Guid tenantId, Guid schoolTenantId, Guid schoolReportId, CancellationToken ct = default)
        => _db.SchoolReports
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r =>
                r.TenantId == tenantId
                && r.SchoolTenantId == schoolTenantId
                && r.SchoolReportId == schoolReportId, ct);

    public async Task<IReadOnlyList<SchoolReport>> ListForSchoolAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
        => await _db.SchoolReports
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.SchoolTenantId == schoolTenantId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<SchoolReport>> ListPendingAsync(int batchSize, CancellationToken ct = default)
        => await _db.SchoolReports
            .IgnoreQueryFilters()
            .Where(r => r.Status == "generating")
            .OrderBy(r => r.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class SchoolReportRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolReportRepository(this IServiceCollection services)
    {
        services.AddScoped<ISchoolReportRepository, SchoolReportRepository>();
        return services;
    }
}
