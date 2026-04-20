using System;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// Single-use email-verification token with a 24h TTL. Consuming a token
/// marks it used and flips the owning <see cref="User"/> from
/// <c>PendingEmailVerification</c> to <c>Active</c>.
/// </summary>
public class EmailVerificationToken
{
    public const int DefaultTtlHours = 24;

    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(DefaultTtlHours);
    public DateTime? UsedAt { get; set; }

    public bool IsUsable =>
        UsedAt is null && ExpiresAt > DateTime.UtcNow;

    public void MarkUsed()
    {
        if (UsedAt is not null)
        {
            throw new InvalidOperationException($"EmailVerificationToken {Id} already used at {UsedAt:O}.");
        }
        if (ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException($"EmailVerificationToken {Id} expired at {ExpiresAt:O}.");
        }
        UsedAt = DateTime.UtcNow;
    }
}
