using System;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Domain.Identity.Entities;

/// <summary>
/// UserSession — one row per device / browser. The <see cref="Id"/>
/// flows into the JWT <c>session_id</c> claim and is consulted by the
/// hot-path session-revocation cache so a logout or admin-forced revoke
/// takes effect before the access-token TTL expires.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string? DeviceName { get; set; }
    public DeviceType DeviceType { get; set; } = DeviceType.Unknown;
    public string IpAddress { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// When set, this session was minted via parent profile-switch
    /// (POST /api/parent/switch-to-child). The value is the parent
    /// session id. Cascade-revocation queries this column to kill
    /// derived child sessions when the parent logs out or rotates
    /// their password.
    /// </summary>
    public Guid? DerivedFromSessionId { get; set; }

    public bool IsActive => RevokedAt is null;

    public void Touch()
    {
        if (RevokedAt is not null)
        {
            throw new InvalidOperationException("Cannot touch a revoked session.");
        }
        LastSeenAt = DateTime.UtcNow;
    }

    public void Revoke()
    {
        RevokedAt ??= DateTime.UtcNow;
    }
}
