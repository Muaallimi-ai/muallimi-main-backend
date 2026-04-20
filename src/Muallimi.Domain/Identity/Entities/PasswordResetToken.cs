using System;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// Single-use password-reset token with a 1h TTL. Consuming a token
/// must revoke every active session for the owning user.
/// </summary>
public class PasswordResetToken
{
    public const int DefaultTtlHours = 1;

    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(DefaultTtlHours);
    public DateTime? UsedAt { get; set; }
    public string? IpAddress { get; set; }

    public bool IsUsable =>
        UsedAt is null && ExpiresAt > DateTime.UtcNow;

    public void MarkUsed()
    {
        if (UsedAt is not null)
        {
            throw new InvalidOperationException($"PasswordResetToken {Id} already used at {UsedAt:O}.");
        }
        if (ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException($"PasswordResetToken {Id} expired at {ExpiresAt:O}.");
        }
        UsedAt = DateTime.UtcNow;
    }
}
