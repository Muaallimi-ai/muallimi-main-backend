using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Credentials;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Credentials;

/// <summary>
/// Shared verification pipeline for <see cref="IManagerReAuthService"/>:
/// rate-limit gate → password check → TOTP gate (when 2FA enrolled) →
/// stamp receipt → clear rate-limit counter on success.
///
/// Subclasses only override the receipt storage —
/// <see cref="StampReceiptAsync"/>, <see cref="HasReceiptAsync"/>,
/// <see cref="ClearReceiptAsync"/> — keeping the security-critical
/// verification logic single-sourced. Redis and in-memory backends
/// are otherwise identical, so any change to the verify flow lands in
/// exactly one place.
/// </summary>
public abstract class ManagerReAuthServiceBase : IManagerReAuthService
{
    protected const string RateLimitScope = "reauth-user";
    protected const int MaxAttempts = 5;
    protected static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(15);

    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly ITwoFactorManagementService _totp;
    private readonly IRateLimitService _rateLimits;

    protected ManagerReAuthServiceBase(
        MuallimiDbContext db,
        IPasswordService passwords,
        ITwoFactorManagementService totp,
        IRateLimitService rateLimits)
    {
        _db = db;
        _passwords = passwords;
        _totp = totp;
        _rateLimits = rateLimits;
    }

    public Task<bool> HasRecentReAuthAsync(Guid managerUserId, CancellationToken ct = default)
        => HasReceiptAsync(managerUserId, ct);

    public Task InvalidateAsync(Guid managerUserId, CancellationToken ct = default)
        => ClearReceiptAsync(managerUserId, ct);

    public async Task<ManagerReAuthOutcome> VerifyAsync(
        Guid managerUserId, string password, string? totpCode, CancellationToken ct = default)
    {
        // Rate-limit gate (5/15min) — short-circuits before any DB read or
        // password compare so brute-force attempts cost as little as possible.
        var decision = await _rateLimits.IncrementAndCheckAsync(
            RateLimitScope, managerUserId.ToString("D"), MaxAttempts, RateWindow, ct).ConfigureAwait(false);
        if (!decision.Allowed) return ManagerReAuthOutcome.RateLimited;

        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == managerUserId, ct).ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
            return ManagerReAuthOutcome.InvalidPassword;
        if (user.Status == UserStatus.Locked) return ManagerReAuthOutcome.Locked;

        // Constant-time password verify with dummy fallback so the timing
        // signature matches the login flow regardless of whether the user
        // record exists or has a hash set.
        if (!_passwords.VerifyWithDummyFallback(password, user.PasswordHash))
            return ManagerReAuthOutcome.InvalidPassword;

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(totpCode)) return ManagerReAuthOutcome.TotpRequired;
            // Delegate to the existing 2FA service which handles decrypt + verify
            // — keeps the secret-cipher handling in a single place.
            if (!await _totp.VerifyTotpCodeAsync(managerUserId, totpCode!, ct).ConfigureAwait(false))
                return ManagerReAuthOutcome.InvalidTotp;
        }

        await StampReceiptAsync(managerUserId, IManagerReAuthService.FreshnessWindow, ct).ConfigureAwait(false);
        await _rateLimits.ClearLockoutAsync($"{RateLimitScope}:{managerUserId:D}", ct).ConfigureAwait(false);
        return ManagerReAuthOutcome.Success;
    }

    protected abstract Task StampReceiptAsync(Guid managerUserId, TimeSpan ttl, CancellationToken ct);
    protected abstract Task<bool> HasReceiptAsync(Guid managerUserId, CancellationToken ct);
    protected abstract Task ClearReceiptAsync(Guid managerUserId, CancellationToken ct);
}
