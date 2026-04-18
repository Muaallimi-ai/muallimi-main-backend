using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Leaderboards.LeaderboardComputation;

/// <summary>
/// T144 (US7) — <see cref="LeaderboardSnapshot"/> repository.
///
/// Snapshots are computed by <c>LeaderboardComputationJob</c> and queried
/// by the role-scoped leaderboard endpoints. Lookups are tenant +
/// school-tenant scoped (IgnoreQueryFilters + explicit predicate) and
/// always return the most recent snapshot for a (scope, metric, subject).
/// </summary>
public interface ILeaderboardSnapshotRepository
{
    Task UpsertLatestAsync(LeaderboardSnapshot snapshot, CancellationToken ct = default);

    Task<LeaderboardSnapshot?> GetLatestAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string scopeType,
        Guid scopeId,
        string metric,
        Guid? subjectId,
        CancellationToken ct = default);

    Task<IReadOnlyList<LeaderboardSnapshot>> ListLatestForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class LeaderboardSnapshotRepository : ILeaderboardSnapshotRepository
{
    private readonly MuallimiDbContext _db;

    public LeaderboardSnapshotRepository(MuallimiDbContext db) => _db = db;

    public async Task UpsertLatestAsync(LeaderboardSnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.LeaderboardSnapshotId == Guid.Empty)
        {
            snapshot.LeaderboardSnapshotId = Guid.NewGuid();
        }
        _db.LeaderboardSnapshots.Add(snapshot);
        await Task.CompletedTask;
    }

    public Task<LeaderboardSnapshot?> GetLatestAsync(
        Guid tenantId,
        Guid schoolTenantId,
        string scopeType,
        Guid scopeId,
        string metric,
        Guid? subjectId,
        CancellationToken ct = default)
        => _db.LeaderboardSnapshots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s =>
                s.TenantId == tenantId
                && s.SchoolTenantId == schoolTenantId
                && s.ScopeType == scopeType
                && s.ScopeId == scopeId
                && s.Metric == metric
                && s.SubjectId == subjectId)
            .OrderByDescending(s => s.ComputedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<LeaderboardSnapshot>> ListLatestForSchoolAsync(
        Guid tenantId,
        Guid schoolTenantId,
        CancellationToken ct = default)
    {
        var rows = await _db.LeaderboardSnapshots
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.SchoolTenantId == schoolTenantId)
            .ToListAsync(ct);

        return rows
            .GroupBy(s => new { s.ScopeType, s.ScopeId, s.Metric, s.SubjectId })
            .Select(g => g.OrderByDescending(s => s.ComputedAt).First())
            .ToList();
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class LeaderboardSnapshotRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5LeaderboardSnapshotRepository(this IServiceCollection services)
    {
        services.AddScoped<ILeaderboardSnapshotRepository, LeaderboardSnapshotRepository>();
        return services;
    }
}
