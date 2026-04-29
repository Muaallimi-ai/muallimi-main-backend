using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Services;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Credentials;

/// <summary>
/// Process-local receipt store for <see cref="ManagerReAuthServiceBase"/>.
/// Local-dev fallback when Redis is not configured. Single-process only;
/// manual TTL check on read.
///
/// The receipt dictionary is <c>static</c> so it survives across scoped
/// service instances — DI registers this class as Scoped because it
/// depends on the scoped <c>MuallimiDbContext</c>, but the freshness
/// receipt itself is a process-wide concern.
/// </summary>
public sealed class InMemoryManagerReAuthService : ManagerReAuthServiceBase
{
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> _receipts = new();

    public InMemoryManagerReAuthService(
        MuallimiDbContext db,
        IPasswordService passwords,
        ITwoFactorManagementService totp,
        IRateLimitService rateLimits)
        : base(db, passwords, totp, rateLimits) { }

    protected override Task StampReceiptAsync(Guid managerUserId, TimeSpan ttl, CancellationToken ct)
    {
        _receipts[managerUserId] = DateTimeOffset.UtcNow.Add(ttl);
        return Task.CompletedTask;
    }

    protected override Task<bool> HasReceiptAsync(Guid managerUserId, CancellationToken ct)
    {
        if (_receipts.TryGetValue(managerUserId, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow)
            return Task.FromResult(true);
        _receipts.TryRemove(managerUserId, out _);
        return Task.FromResult(false);
    }

    protected override Task ClearReceiptAsync(Guid managerUserId, CancellationToken ct)
    {
        _receipts.TryRemove(managerUserId, out _);
        return Task.CompletedTask;
    }
}
