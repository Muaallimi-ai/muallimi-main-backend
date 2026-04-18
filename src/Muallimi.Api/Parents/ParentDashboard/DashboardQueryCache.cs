using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Parents.ParentDashboard;

/// <summary>
/// T016 — Short-TTL parent dashboard query cache.
///
/// Keyed by <c>(tenant_id, parent_id, child_id)</c>. The dashboard query
/// aggregates mastery, focus areas, recent activity, and the latest weekly
/// report tile; hitting the DB on every frame is wasteful given that the
/// underlying state only changes on Phase 4 downstream events. The cache
/// invalidates on <c>mastery_updated</c>, <c>focus_area_updated</c>,
/// <c>weekly_report_generated</c>, <c>streak_changed</c>, <c>badge_awarded</c>,
/// <c>at_risk_flagged</c>, and <c>at_risk_cleared</c> events.
///
/// In production the cache is served by Azure Cache for Redis; local parity
/// keeps us on an in-process <see cref="ConcurrentDictionary"/> so the full
/// Phase 4 walkthrough runs without any external cache dependency.
/// </summary>
public interface IDashboardQueryCache
{
    Task<T?> GetAsync<T>(DashboardQueryCacheKey key, CancellationToken ct = default) where T : class;
    Task SetAsync<T>(DashboardQueryCacheKey key, T value, TimeSpan? ttl = null, CancellationToken ct = default) where T : class;
    Task InvalidateChildAsync(Guid tenantId, Guid childId, CancellationToken ct = default);
    Task InvalidateParentAsync(Guid tenantId, Guid parentId, CancellationToken ct = default);
}

public readonly record struct DashboardQueryCacheKey(
    Guid TenantId,
    Guid ParentId,
    Guid ChildId,
    string Slot);

public sealed class DashboardQueryCache : IDashboardQueryCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<DashboardQueryCacheKey, Entry> _entries = new();

    public Task<T?> GetAsync<T>(DashboardQueryCacheKey key, CancellationToken ct = default) where T : class
    {
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow && entry.Value is T typed)
        {
            return Task.FromResult<T?>(typed);
        }
        _entries.TryRemove(key, out _);
        return Task.FromResult<T?>(null);
    }

    public Task SetAsync<T>(DashboardQueryCacheKey key, T value, TimeSpan? ttl = null, CancellationToken ct = default) where T : class
    {
        var expires = DateTime.UtcNow + (ttl ?? DefaultTtl);
        _entries[key] = new Entry(value, expires);
        return Task.CompletedTask;
    }

    public Task InvalidateChildAsync(Guid tenantId, Guid childId, CancellationToken ct = default)
    {
        foreach (var key in _entries.Keys)
        {
            if (key.TenantId == tenantId && key.ChildId == childId)
            {
                _entries.TryRemove(key, out _);
            }
        }
        return Task.CompletedTask;
    }

    public Task InvalidateParentAsync(Guid tenantId, Guid parentId, CancellationToken ct = default)
    {
        foreach (var key in _entries.Keys)
        {
            if (key.TenantId == tenantId && key.ParentId == parentId)
            {
                _entries.TryRemove(key, out _);
            }
        }
        return Task.CompletedTask;
    }

    private sealed record Entry(object Value, DateTime ExpiresAt);
}

public static class DashboardQueryCacheServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4DashboardQueryCache(this IServiceCollection services)
    {
        services.AddSingleton<IDashboardQueryCache, DashboardQueryCache>();
        return services;
    }
}
