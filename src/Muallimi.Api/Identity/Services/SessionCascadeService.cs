using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Services;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// Add-child redesign Phase 5 — cascade revocation. Profile-switch
/// child sessions are derived from a parent session via
/// <c>UserSession.DerivedFromSessionId</c>; when the parent session
/// ends (logout, password change, admin revoke) every derived child
/// session must die — both the access-side <c>UserSession</c> row AND
/// the refresh-token rows in <c>IdentityRefreshTokens</c>.
/// </summary>
public interface ISessionCascadeService
{
    /// <summary>Revoke every derived session whose <c>DerivedFromSessionId</c> matches the given parent session.</summary>
    Task RevokeDerivedFromAsync(Guid parentSessionId, CancellationToken ct = default);

    /// <summary>
    /// Revoke every derived session for any of the user's currently-active sessions —
    /// used on parent password change where we don't know the active session id.
    /// </summary>
    Task RevokeAllDerivedFromUserAsync(Guid parentUserId, CancellationToken ct = default);
}

public sealed class SessionCascadeService : ISessionCascadeService
{
    private readonly MuallimiDbContext _db;
    private readonly ISessionActivityCache _cache;

    public SessionCascadeService(MuallimiDbContext db, ISessionActivityCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task RevokeDerivedFromAsync(Guid parentSessionId, CancellationToken ct = default)
    {
        var derivedSessions = await _db.IdentityUserSessions.IgnoreQueryFilters()
            .Where(s => s.DerivedFromSessionId == parentSessionId && s.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (derivedSessions.Count == 0) return;

        foreach (var s in derivedSessions) s.Revoke();

        var derivedIds = derivedSessions.Select(s => s.Id).ToList();
        var refreshTokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => derivedIds.Contains(t.SessionId) && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in refreshTokens) t.MarkFamilyRevoked();

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var id in derivedIds)
        {
            await _cache.InvalidateAsync(id, ct).ConfigureAwait(false);
        }
    }

    public async Task RevokeAllDerivedFromUserAsync(Guid parentUserId, CancellationToken ct = default)
    {
        var parentSessionIds = await _db.IdentityUserSessions.IgnoreQueryFilters()
            .Where(s => s.UserId == parentUserId)
            .Select(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        if (parentSessionIds.Count == 0) return;

        var derivedSessions = await _db.IdentityUserSessions.IgnoreQueryFilters()
            .Where(s => s.DerivedFromSessionId != null
                     && parentSessionIds.Contains(s.DerivedFromSessionId!.Value)
                     && s.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (derivedSessions.Count == 0) return;

        foreach (var s in derivedSessions) s.Revoke();

        var derivedIds = derivedSessions.Select(s => s.Id).ToList();
        var refreshTokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => derivedIds.Contains(t.SessionId) && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in refreshTokens) t.MarkFamilyRevoked();

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var id in derivedIds)
        {
            await _cache.InvalidateAsync(id, ct).ConfigureAwait(false);
        }
    }
}
