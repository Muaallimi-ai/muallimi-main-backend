using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.StreakCalculation;

/// <summary>
/// T035 (US4) — <see cref="StreakState"/> repository.
///
/// One row per (tenant_id, student_id). The streak is authoritative in the
/// family's IANA timezone (see <see cref="FamilyTimezoneResolver"/>).
/// </summary>
public interface IStreakStateRepository
{
    Task<StreakState?> GetAsync(Guid tenantId, Guid studentId, CancellationToken ct = default);
    Task AddAsync(StreakState state, CancellationToken ct = default);
}

public sealed class StreakStateRepository : IStreakStateRepository
{
    private readonly MuallimiDbContext _db;

    public StreakStateRepository(MuallimiDbContext db) => _db = db;

    public Task<StreakState?> GetAsync(Guid tenantId, Guid studentId, CancellationToken ct = default)
        => _db.StreakStates
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.StudentId == studentId, ct);

    public Task AddAsync(StreakState state, CancellationToken ct = default)
    {
        _db.StreakStates.Add(state);
        return Task.CompletedTask;
    }
}

public static class StreakStateRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4StreakStateRepository(this IServiceCollection services)
    {
        services.AddScoped<IStreakStateRepository, StreakStateRepository>();
        return services;
    }
}
