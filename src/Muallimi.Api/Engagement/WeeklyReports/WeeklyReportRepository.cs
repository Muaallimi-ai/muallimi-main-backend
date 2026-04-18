using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T090 (US3) — <see cref="WeeklyReport"/> repository.
///
/// Keyed by the database UNIQUE
/// <c>(tenant_id, student_id, window_start, window_end)</c>. At most one
/// row in <c>ready</c> status MAY exist per window; regeneration marks
/// the prior row <c>regenerating</c> and produces a new <c>run_id</c>.
/// </summary>
public interface IWeeklyReportRepository
{
    Task<WeeklyReport?> GetByIdAsync(Guid tenantId, Guid weeklyReportId, CancellationToken ct = default);

    Task<WeeklyReport?> GetByWindowAsync(
        Guid tenantId,
        Guid studentId,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken ct = default);

    Task<IReadOnlyList<WeeklyReport>> ListForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default);

    Task AddAsync(WeeklyReport report, CancellationToken ct = default);

    Task UpdateAsync(WeeklyReport report, CancellationToken ct = default);
}

public sealed class WeeklyReportRepository : IWeeklyReportRepository
{
    private readonly MuallimiDbContext _db;

    public WeeklyReportRepository(MuallimiDbContext db) => _db = db;

    public Task<WeeklyReport?> GetByIdAsync(Guid tenantId, Guid weeklyReportId, CancellationToken ct = default)
        => _db.WeeklyReports
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.WeeklyReportId == weeklyReportId, ct);

    public Task<WeeklyReport?> GetByWindowAsync(
        Guid tenantId,
        Guid studentId,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken ct = default)
        => _db.WeeklyReports
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId
                     && r.StudentId == studentId
                     && r.WindowStart == windowStart.Date
                     && r.WindowEnd == windowEnd.Date,
                ct);

    public async Task<IReadOnlyList<WeeklyReport>> ListForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default)
    {
        return await _db.WeeklyReports
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.StudentId == studentId)
            .OrderByDescending(r => r.WindowEnd)
            .ToListAsync(ct);
    }

    public async Task AddAsync(WeeklyReport report, CancellationToken ct = default)
    {
        _db.WeeklyReports.Add(report);
        await Task.CompletedTask;
    }

    public async Task UpdateAsync(WeeklyReport report, CancellationToken ct = default)
    {
        _db.WeeklyReports.Update(report);
        await Task.CompletedTask;
    }
}

public static class WeeklyReportRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4WeeklyReportRepository(this IServiceCollection services)
    {
        services.AddScoped<IWeeklyReportRepository, WeeklyReportRepository>();
        return services;
    }
}
