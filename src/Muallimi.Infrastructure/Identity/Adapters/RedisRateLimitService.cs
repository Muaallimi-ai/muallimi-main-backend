using System;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Application.Identity.Services;
using StackExchange.Redis;

namespace Muallimi.Infrastructure.Identity.Adapters;

/// <summary>
/// T032 — Sliding-window rate limiter backed by Redis. Counters live under
/// <c>rate:{scope}:{key}</c> keys with a TTL equal to the window length.
/// Lockout state (post-5-failures) is a separate <c>lockout:{userId}</c>
/// key the caller reads/sets as an explicit boolean + expiry so
/// <c>AuthService.Login</c> can short-circuit without incrementing the
/// ordinary counter. Interface + <c>RateLimitDecision</c> record live
/// in <see cref="Muallimi.Application.Identity.Services.IRateLimitService"/>.
/// </summary>
public sealed class RedisRateLimitService : IRateLimitService
{
    private const string CounterPrefix = "rate:";
    private const string LockoutPrefix = "lockout:";

    private readonly IConnectionMultiplexer _redis;

    public RedisRateLimitService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<RateLimitDecision> IncrementAndCheckAsync(
        string scope,
        string key,
        int maxAttempts,
        TimeSpan window,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var redisKey = $"{CounterPrefix}{scope}:{key}";
        var db = _redis.GetDatabase();
        var count = await db.StringIncrementAsync(redisKey).ConfigureAwait(false);
        if (count == 1)
        {
            await db.KeyExpireAsync(redisKey, window).ConfigureAwait(false);
        }
        var ttl = await db.KeyTimeToLiveAsync(redisKey).ConfigureAwait(false);
        var allowed = count <= maxAttempts;
        return new RateLimitDecision(
            Allowed: allowed,
            CurrentCount: count,
            MaxAttempts: maxAttempts,
            RetryAfter: allowed ? null : ttl);
    }

    public async Task<bool> IsLockedOutAsync(string userIdentifier, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        return await db.KeyExistsAsync($"{LockoutPrefix}{userIdentifier}").ConfigureAwait(false);
    }

    public async Task LockOutAsync(string userIdentifier, TimeSpan duration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        await db.StringSetAsync($"{LockoutPrefix}{userIdentifier}", "1", expiry: duration).ConfigureAwait(false);
    }

    public async Task ClearLockoutAsync(string userIdentifier, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var db = _redis.GetDatabase();
        await db.KeyDeleteAsync($"{LockoutPrefix}{userIdentifier}").ConfigureAwait(false);
    }
}
