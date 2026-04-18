using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Leaderboards.LeaderboardQuery;

/// <summary>
/// T147 (US7) — <see cref="LeaderboardConfig"/> repository.
///
/// Configs are 1:1 with <see cref="SchoolTenant"/>. When a school has no
/// row yet, callers receive the default config (first-name-only, enabled,
/// no overrides) per the contract invariants.
/// </summary>
public interface ILeaderboardConfigRepository
{
    Task<LeaderboardConfig> GetOrDefaultAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default);

    Task UpsertAsync(LeaderboardConfig row, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public sealed class LeaderboardConfigRepository : ILeaderboardConfigRepository
{
    private readonly MuallimiDbContext _db;

    public LeaderboardConfigRepository(MuallimiDbContext db) => _db = db;

    public async Task<LeaderboardConfig> GetOrDefaultAsync(Guid tenantId, Guid schoolTenantId, CancellationToken ct = default)
    {
        var existing = await _db.LeaderboardConfigs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.SchoolTenantId == schoolTenantId, ct);

        return existing ?? new LeaderboardConfig
        {
            LeaderboardConfigId = Guid.Empty,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            PrivacyMode = "first_name_only",
            LeaderboardEnabled = true,
            PerClassOverridesJson = "[]",
            UpdatedAt = DateTime.UtcNow,
        };
    }

    public async Task UpsertAsync(LeaderboardConfig row, CancellationToken ct = default)
    {
        var existing = await _db.LeaderboardConfigs
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == row.TenantId && c.SchoolTenantId == row.SchoolTenantId, ct);

        if (existing is null)
        {
            if (row.LeaderboardConfigId == Guid.Empty) row.LeaderboardConfigId = Guid.NewGuid();
            row.UpdatedAt = DateTime.UtcNow;
            _db.LeaderboardConfigs.Add(row);
            return;
        }

        existing.PrivacyMode = row.PrivacyMode;
        existing.LeaderboardEnabled = row.LeaderboardEnabled;
        existing.PerClassOverridesJson = row.PerClassOverridesJson;
        existing.UpdatedAt = DateTime.UtcNow;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}

public static class LeaderboardConfigRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5LeaderboardConfigRepository(this IServiceCollection services)
    {
        services.AddScoped<ILeaderboardConfigRepository, LeaderboardConfigRepository>();
        return services;
    }
}
