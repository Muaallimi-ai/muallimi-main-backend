using System;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// No-op <see cref="IRateLimitService"/> used in environments without
/// Redis (local dev, unit tests). Every call is allowed; lockouts never
/// fire. Production deployments MUST bind the Redis-backed
/// <c>RedisRateLimitService</c> via <c>AddIdentityModule</c>.
/// </summary>
public sealed class NullRateLimitService : IRateLimitService
{
    public Task<RateLimitDecision> IncrementAndCheckAsync(
        string scope, string key, int maxAttempts, TimeSpan window, CancellationToken ct = default)
        => Task.FromResult(new RateLimitDecision(Allowed: true, CurrentCount: 0, MaxAttempts: maxAttempts, RetryAfter: null));

    public Task<bool> IsLockedOutAsync(string userIdentifier, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task LockOutAsync(string userIdentifier, TimeSpan duration, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task ClearLockoutAsync(string userIdentifier, CancellationToken ct = default)
        => Task.CompletedTask;
}
