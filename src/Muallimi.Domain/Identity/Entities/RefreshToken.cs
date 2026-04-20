using System;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// RefreshToken — single-use bearer token rotated on each refresh.
/// Tokens within a single session form a "family" chain via
/// <see cref="SessionId"/>; reuse of a revoked token triggers
/// full-family revocation (FR-033).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsActive =>
        RevokedAt is null && ExpiresAt > DateTime.UtcNow;

    public void MarkRotated(Guid replacementTokenId)
    {
        EnsureNotRevoked();
        RevokedAt = DateTime.UtcNow;
        RevokedReason = "rotated";
        ReplacedByTokenId = replacementTokenId;
    }

    public void MarkLoggedOut()
    {
        EnsureNotRevoked();
        RevokedAt = DateTime.UtcNow;
        RevokedReason = "logout";
    }

    public void MarkExpired()
    {
        if (RevokedAt is not null) return;
        RevokedAt = DateTime.UtcNow;
        RevokedReason = "expired";
    }

    public void MarkCompromised()
    {
        RevokedAt ??= DateTime.UtcNow;
        RevokedReason = "compromised";
    }

    public void MarkFamilyRevoked()
    {
        RevokedAt ??= DateTime.UtcNow;
        RevokedReason = "family-revoked";
    }

    private void EnsureNotRevoked()
    {
        if (RevokedAt is not null)
        {
            throw new InvalidOperationException($"RefreshToken {Id} already revoked (reason: {RevokedReason}).");
        }
    }
}
