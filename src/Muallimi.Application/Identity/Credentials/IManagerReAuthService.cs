using System;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Credentials;

/// <summary>
/// Step-up re-auth gate for managers (parents today, school admins
/// later) before they can perform a destructive credential action on
/// a managed user — reset password, reset PIN, add PIN, upgrade tier.
///
/// A manager must have re-authenticated within the freshness window
/// (<see cref="FreshnessWindow"/>) to be allowed to act. The freshness
/// receipt is keyed on the manager's user ID and is invalidated by
/// any password / 2FA change on the manager account.
///
/// Failure paths emit <see cref="CredentialAuditEventKind.ParentReauthFailed"/>
/// via <see cref="ICredentialAuditWriter"/> and are rate-limited via
/// the existing <c>IRateLimitService</c> with key
/// <c>reauth:{managerUserId}</c> (5 attempts per 15 minutes).
/// </summary>
public interface IManagerReAuthService
{
    /// <summary>
    /// Recency window during which a successful re-auth allows
    /// destructive credential actions without prompting again.
    /// </summary>
    static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns true if the manager re-authenticated within the
    /// freshness window. False means the API must prompt for
    /// password (and TOTP if 2FA is enrolled) before proceeding.
    /// </summary>
    Task<bool> HasRecentReAuthAsync(Guid managerUserId, CancellationToken ct = default);

    /// <summary>
    /// Validate a password (and optional TOTP code) against the
    /// manager's account. On success, stamps the freshness receipt
    /// and returns <see cref="ManagerReAuthOutcome.Success"/>.
    /// On failure, returns the specific outcome and increments the
    /// rate-limit counter.
    /// </summary>
    Task<ManagerReAuthOutcome> VerifyAsync(
        Guid managerUserId,
        string password,
        string? totpCode,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidate the freshness receipt — called after a password
    /// change or 2FA enrollment change on the manager account.
    /// </summary>
    Task InvalidateAsync(Guid managerUserId, CancellationToken ct = default);
}

public enum ManagerReAuthOutcome
{
    Success,
    InvalidPassword,
    InvalidTotp,
    TotpRequired,
    Locked,
    RateLimited,
}
