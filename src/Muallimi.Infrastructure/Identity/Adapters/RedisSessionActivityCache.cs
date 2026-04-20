using System;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using Muallimi.Application.Identity.Services;

namespace Muallimi.Infrastructure.Identity.Adapters;

/// <summary>
/// Redis-backed <see cref="ISessionActivityCache"/>. Keys live under
/// <c>session:active:{id}</c> and store a single byte (<c>"1"</c> for
/// active, <c>"0"</c> for revoked). Invalidation deletes the key so the
/// next probe falls through to the repository and re-caches.
/// </summary>
public sealed class RedisSessionActivityCache : ISessionActivityCache
{
    private const string KeyPrefix = "session:active:";

    private readonly IConnectionMultiplexer _redis;

    public RedisSessionActivityCache(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<bool?> TryGetActiveAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(Key(sessionId)).ConfigureAwait(false);
        if (!value.HasValue) return null;
        return value == "1";
    }

    public async Task SetActiveAsync(Guid sessionId, bool isActive, TimeSpan ttl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        await db.StringSetAsync(Key(sessionId), isActive ? "1" : "0", expiry: ttl).ConfigureAwait(false);
    }

    public async Task InvalidateAsync(Guid sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync(Key(sessionId)).ConfigureAwait(false);
    }

    private static string Key(Guid sessionId) => $"{KeyPrefix}{sessionId:D}";
}

/// <summary>
/// No-op in-memory fallback used by tests and local-dev environments
/// that haven't wired Redis. Thread-safe via a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
public sealed class InMemorySessionActivityCache : ISessionActivityCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (bool Active, DateTime ExpiresAt)> _store = new();

    public Task<bool?> TryGetActiveAsync(Guid sessionId, CancellationToken ct)
    {
        if (_store.TryGetValue(sessionId, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            return Task.FromResult<bool?>(entry.Active);
        }
        return Task.FromResult<bool?>(null);
    }

    public Task SetActiveAsync(Guid sessionId, bool isActive, TimeSpan ttl, CancellationToken ct)
    {
        _store[sessionId] = (isActive, DateTime.UtcNow.Add(ttl));
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(Guid sessionId, CancellationToken ct)
    {
        _store.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }
}
