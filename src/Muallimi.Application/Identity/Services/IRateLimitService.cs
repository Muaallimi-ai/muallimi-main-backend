using System;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// Rate-limiting abstraction. Callers set <paramref name="scope"/> to
/// isolate counters per endpoint (e.g., <c>login-ip</c>,
/// <c>login-user</c>, <c>refresh-user</c>). Lockout state (post-5
/// failures) is a separate boolean + expiry so hot-path login code can
/// short-circuit without incrementing the ordinary counter.
/// </summary>
public interface IRateLimitService
{
    Task<RateLimitDecision> IncrementAndCheckAsync(
        string scope,
        string key,
        int maxAttempts,
        TimeSpan window,
        CancellationToken ct = default);

    Task<bool> IsLockedOutAsync(string userIdentifier, CancellationToken ct = default);
    Task LockOutAsync(string userIdentifier, TimeSpan duration, CancellationToken ct = default);
    Task ClearLockoutAsync(string userIdentifier, CancellationToken ct = default);
}

public sealed record RateLimitDecision(
    bool Allowed,
    long CurrentCount,
    int MaxAttempts,
    TimeSpan? RetryAfter);
