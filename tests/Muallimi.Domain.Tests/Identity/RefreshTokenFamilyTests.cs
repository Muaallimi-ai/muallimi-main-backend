using System;
using Muallimi.Domain.Identity.Entities;
using Xunit;

namespace Muallimi.Domain.Tests.Identity;

/// <summary>
/// T059 — Domain tests for <see cref="RefreshToken"/> rotation chain and
/// reuse-detection state.
///
/// Covers:
///   • rotation chain — MarkRotated sets RevokedAt, reason "rotated",
///     and ReplacedByTokenId pointing at the successor;
///   • terminal revocation reasons — "logout", "expired", "compromised",
///     "family-revoked";
///   • reuse detection — MarkFamilyRevoked can be called on an already
///     revoked token (e.g., a stolen token whose family must die);
///   • one-way transitions — cannot rotate/logout a token that has
///     already been revoked.
/// </summary>
public class RefreshTokenFamilyTests
{
    private static RefreshToken NewActive(Guid? sessionId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        SessionId = sessionId ?? Guid.NewGuid(),
        TokenHash = "hash-" + Guid.NewGuid().ToString("N"),
        IssuedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(7),
    };

    [Fact]
    public void Fresh_Token_Is_Active()
    {
        var t = NewActive();
        Assert.True(t.IsActive);
        Assert.Null(t.RevokedAt);
        Assert.Null(t.RevokedReason);
        Assert.Null(t.ReplacedByTokenId);
    }

    [Fact]
    public void MarkRotated_Creates_Forward_Link_In_Family()
    {
        var session = Guid.NewGuid();
        var a = NewActive(session);
        var b = NewActive(session);

        a.MarkRotated(b.Id);

        Assert.False(a.IsActive);
        Assert.NotNull(a.RevokedAt);
        Assert.Equal("rotated", a.RevokedReason);
        Assert.Equal(b.Id, a.ReplacedByTokenId);

        // Successor still active.
        Assert.True(b.IsActive);
    }

    [Fact]
    public void Rotation_Chain_Forms_Family()
    {
        var session = Guid.NewGuid();
        var a = NewActive(session);
        var b = NewActive(session);
        var c = NewActive(session);

        a.MarkRotated(b.Id);
        b.MarkRotated(c.Id);

        Assert.Equal(b.Id, a.ReplacedByTokenId);
        Assert.Equal(c.Id, b.ReplacedByTokenId);
        Assert.True(c.IsActive);
        Assert.Equal(session, a.SessionId);
        Assert.Equal(session, b.SessionId);
        Assert.Equal(session, c.SessionId);
    }

    [Fact]
    public void MarkRotated_Rejects_Already_Revoked_Token()
    {
        var a = NewActive();
        a.MarkLoggedOut();
        Assert.Throws<InvalidOperationException>(() => a.MarkRotated(Guid.NewGuid()));
    }

    [Fact]
    public void MarkLoggedOut_Revokes_With_Logout_Reason()
    {
        var t = NewActive();
        t.MarkLoggedOut();

        Assert.False(t.IsActive);
        Assert.Equal("logout", t.RevokedReason);
    }

    [Fact]
    public void MarkLoggedOut_Rejects_Already_Revoked_Token()
    {
        var t = NewActive();
        t.MarkLoggedOut();
        Assert.Throws<InvalidOperationException>(() => t.MarkLoggedOut());
    }

    [Fact]
    public void MarkExpired_Sets_Expired_Reason_Once()
    {
        var t = NewActive();
        t.MarkExpired();

        Assert.Equal("expired", t.RevokedReason);
        var firstRevokedAt = t.RevokedAt;

        // Idempotent: second call is a no-op.
        t.MarkExpired();
        Assert.Equal(firstRevokedAt, t.RevokedAt);
    }

    [Fact]
    public void MarkCompromised_Sets_Compromised_Reason_On_Fresh_Token()
    {
        var t = NewActive();
        t.MarkCompromised();

        Assert.False(t.IsActive);
        Assert.Equal("compromised", t.RevokedReason);
        Assert.NotNull(t.RevokedAt);
    }

    [Fact]
    public void MarkCompromised_Overrides_Reason_On_Already_Revoked_Token()
    {
        // Reuse-detection: a previously rotated token that is replayed
        // must be re-flagged as compromised.
        var a = NewActive();
        a.MarkRotated(Guid.NewGuid());
        var rotationTime = a.RevokedAt;

        a.MarkCompromised();

        Assert.Equal("compromised", a.RevokedReason);
        // Original revoked-at timestamp preserved (token was already dead).
        Assert.Equal(rotationTime, a.RevokedAt);
    }

    [Fact]
    public void MarkFamilyRevoked_Works_On_Active_And_Revoked_Tokens()
    {
        var active = NewActive();
        active.MarkFamilyRevoked();
        Assert.Equal("family-revoked", active.RevokedReason);
        Assert.False(active.IsActive);

        // Also applies on an already-revoked token — e.g. when the entire
        // session family must be invalidated after detecting reuse.
        var rotated = NewActive();
        rotated.MarkRotated(Guid.NewGuid());
        rotated.MarkFamilyRevoked();
        Assert.Equal("family-revoked", rotated.RevokedReason);
    }

    [Fact]
    public void IsActive_Respects_Expiry()
    {
        var t = NewActive();
        t.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        Assert.False(t.IsActive);
    }
}
