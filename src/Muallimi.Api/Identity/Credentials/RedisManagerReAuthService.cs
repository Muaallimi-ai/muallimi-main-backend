using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Services;
using Muallimi.Infrastructure.Persistence;
using StackExchange.Redis;

namespace Muallimi.Api.Identity.Credentials;

/// <summary>
/// Redis-backed receipt store for <see cref="ManagerReAuthServiceBase"/>.
/// The verification pipeline (rate limit + password + TOTP) lives in
/// the base class — this class only swaps in Redis for the freshness
/// receipt with a native TTL. Production binding.
/// </summary>
public sealed class RedisManagerReAuthService : ManagerReAuthServiceBase
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisManagerReAuthService> _logger;

    public RedisManagerReAuthService(
        IConnectionMultiplexer redis,
        MuallimiDbContext db,
        IPasswordService passwords,
        ITwoFactorManagementService totp,
        IRateLimitService rateLimits,
        ILogger<RedisManagerReAuthService> logger)
        : base(db, passwords, totp, rateLimits)
    {
        _redis = redis;
        _logger = logger;
    }

    private static string ReceiptKey(Guid managerUserId) =>
        $"reauth:freshness:{managerUserId:D}";

    protected override async Task StampReceiptAsync(Guid managerUserId, TimeSpan ttl, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        // Stored value is the issued-at timestamp (forensic only — never a secret).
        await db.StringSetAsync(ReceiptKey(managerUserId), DateTimeOffset.UtcNow.ToString("O"), ttl).ConfigureAwait(false);
    }

    protected override async Task<bool> HasReceiptAsync(Guid managerUserId, CancellationToken ct)
    {
        var db = _redis.GetDatabase();
        return await db.KeyExistsAsync(ReceiptKey(managerUserId)).ConfigureAwait(false);
    }

    protected override async Task ClearReceiptAsync(Guid managerUserId, CancellationToken ct)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(ReceiptKey(managerUserId)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive: never fail a calling password change because of a
            // Redis blip. The freshness window is 5 min so a missed
            // invalidation has bounded impact.
            _logger.LogWarning(ex, "Failed to invalidate reauth receipt for {ManagerUserId}", managerUserId);
        }
    }
}
