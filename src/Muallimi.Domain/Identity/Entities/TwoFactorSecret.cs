using System;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// TwoFactorSecret — per-user TOTP secret + recovery codes, encrypted at
/// rest by <c>AesEncryptor</c>. Unique on <see cref="UserId"/>.
/// Super-admin cannot disable 2FA while still super-admin (FR-015);
/// Managed (student) accounts cannot enrol.
/// </summary>
public class TwoFactorSecret
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Encrypted TOTP secret (AES-256).</summary>
    public string Secret { get; set; } = string.Empty;
    /// <summary>Encrypted JSON array of 10 single-use recovery codes.</summary>
    public string RecoveryCodes { get; set; } = string.Empty;
    public DateTime EnabledAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
