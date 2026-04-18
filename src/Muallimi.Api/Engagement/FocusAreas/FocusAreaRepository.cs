using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.FocusAreas;

/// <summary>
/// T107 (US5) — <see cref="FocusArea"/> repository.
///
/// Focus areas are tenant + student scoped. The calculator writes a fresh
/// set per refresh tick; stale rows beyond <c>valid_until</c> are cleared
/// inside the same unit of work so the student progress surface and the
/// parent dashboard never render expired guidance.
/// </summary>
public interface IFocusAreaRepository
{
    Task<IReadOnlyList<FocusArea>> ListActiveAsync(
        Guid tenantId,
        Guid studentId,
        DateTime nowUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<FocusArea>> ListForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default);

    Task AddAsync(FocusArea focusArea, CancellationToken ct = default);

    Task RemoveRangeAsync(IEnumerable<FocusArea> focusAreas, CancellationToken ct = default);
}

public sealed class FocusAreaRepository : IFocusAreaRepository
{
    private readonly MuallimiDbContext _db;

    public FocusAreaRepository(MuallimiDbContext db) => _db = db;

    public async Task<IReadOnlyList<FocusArea>> ListActiveAsync(
        Guid tenantId,
        Guid studentId,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var asOf = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return await _db.FocusAreas
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.TenantId == tenantId
                        && f.StudentId == studentId
                        && f.ValidUntil > asOf)
            .OrderBy(f => f.ComputedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FocusArea>> ListForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default)
    {
        return await _db.FocusAreas
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.StudentId == studentId)
            .ToListAsync(ct);
    }

    public Task AddAsync(FocusArea focusArea, CancellationToken ct = default)
    {
        _db.FocusAreas.Add(focusArea);
        return Task.CompletedTask;
    }

    public Task RemoveRangeAsync(IEnumerable<FocusArea> focusAreas, CancellationToken ct = default)
    {
        _db.FocusAreas.RemoveRange(focusAreas);
        return Task.CompletedTask;
    }
}

public static class FocusAreaRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4FocusAreaRepository(this IServiceCollection services)
    {
        services.AddScoped<IFocusAreaRepository, FocusAreaRepository>();
        return services;
    }
}
