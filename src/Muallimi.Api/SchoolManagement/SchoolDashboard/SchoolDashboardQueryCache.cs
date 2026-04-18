using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.SchoolManagement.SchoolDashboard;

/// <summary>
/// T020 — <c>SchoolDashboardQueryCache</c>.
///
/// Extends the Phase 4 <c>DashboardQueryCache</c> pattern for school-scoped
/// queries. A per-process in-memory cache keyed by
/// <c>(school_tenant_id, scope_type, scope_id, query_name)</c> returns the
/// cached payload with a TTL. Cache miss computes the value via the
/// factory delegate and stores it with the TTL. Exposed via
/// <see cref="ISchoolDashboardQueryCache"/> so US4 (T090) can back the
/// dashboard service with consistent hot-path caching.
///
/// Invalidation: the Phase 4 event consumer (T014/T015) clears keys whose
/// scope matches the incoming event by calling
/// <see cref="Invalidate"/>.
/// </summary>
public interface ISchoolDashboardQueryCache
{
    Task<T> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default);
    void Invalidate(string prefix);
}

public sealed class SchoolDashboardQueryCache : ISchoolDashboardQueryCache
{
    private sealed record CacheEntry(object Value, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _store = new(StringComparer.Ordinal);

    public async Task<T> GetOrSetAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken ct = default)
    {
        if (_store.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow && entry.Value is T cached)
        {
            return cached;
        }
        var fresh = await factory(ct);
        _store[key] = new CacheEntry(fresh!, DateTime.UtcNow.Add(ttl));
        return fresh;
    }

    public void Invalidate(string prefix)
    {
        foreach (var kv in _store)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _store.TryRemove(kv.Key, out _);
            }
        }
    }
}

public static class SchoolDashboardQueryCacheServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolDashboardQueryCache(this IServiceCollection services)
    {
        services.AddSingleton<ISchoolDashboardQueryCache, SchoolDashboardQueryCache>();
        return services;
    }
}
