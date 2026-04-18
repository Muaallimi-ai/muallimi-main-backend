using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.OperatorManagement.FeatureFlags;

/// <summary>
/// T027 — Per-tenant feature flag read/write with a 30-second cache. Toggle
/// operations immediately invalidate the cache so operator changes take
/// effect without waiting for TTL.
/// </summary>
public class FeatureFlagService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly MuallimiDbContext _db;
    private readonly IMemoryCache _cache;

    public FeatureFlagService(MuallimiDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<bool> IsEnabledAsync(Guid tenantId, string flagName, CancellationToken ct = default)
    {
        var key = CacheKey(tenantId, flagName);
        if (_cache.TryGetValue(key, out bool cached)) return cached;

        var flag = await _db.FeatureFlags
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FlagName == flagName, ct);

        var enabled = flag?.IsEnabled ?? false;
        _cache.Set(key, enabled, CacheTtl);
        return enabled;
    }

    public async Task SetAsync(Guid tenantId, string flagName, bool isEnabled, Guid operatorId, CancellationToken ct = default)
    {
        var flag = await _db.FeatureFlags.FirstOrDefaultAsync(
            f => f.TenantId == tenantId && f.FlagName == flagName, ct);

        var now = DateTime.UtcNow;
        if (flag is null)
        {
            _db.FeatureFlags.Add(new FeatureFlag
            {
                FeatureFlagId = Guid.NewGuid(),
                TenantId = tenantId,
                FlagName = flagName,
                IsEnabled = isEnabled,
                ChangedByOperatorId = operatorId,
                ChangedAt = now,
                CreatedAt = now
            });
        }
        else
        {
            flag.IsEnabled = isEnabled;
            flag.ChangedByOperatorId = operatorId;
            flag.ChangedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        _cache.Remove(CacheKey(tenantId, flagName));
    }

    private static string CacheKey(Guid tenantId, string flagName) => $"ff:{tenantId:N}:{flagName}";
}
