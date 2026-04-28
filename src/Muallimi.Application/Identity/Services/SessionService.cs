using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T035 — Session lifecycle service. Creates and revokes
/// <see cref="UserSession"/> rows, and owns the <c>IsSessionActive</c>
/// hot-path check the <c>IdentityClaimsReader</c> middleware calls on
/// every authenticated request.
///
/// The <see cref="ISessionActivityCache"/> abstraction lets the Api
/// layer bind a Redis-backed implementation (production) or an
/// in-memory dictionary (tests) without the Application layer
/// referencing <c>StackExchange.Redis</c>.
/// </summary>
public interface ISessionService
{
    Task<UserSession> CreateAsync(CreateSessionInput input, CancellationToken ct = default);
    Task RevokeAsync(Guid sessionId, CancellationToken ct = default);
    Task RevokeAllForUserAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct = default);
    Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default);
    Task TouchAsync(Guid sessionId, CancellationToken ct = default);
    /// <summary>T132 — List all active sessions for a user (for self-service sessions view).</summary>
    Task<IReadOnlyList<UserSession>> ListActiveSessionsAsync(Guid userId, CancellationToken ct = default);
}

public sealed record CreateSessionInput(
    Guid UserId,
    string IpAddress,
    string? UserAgent,
    string? DeviceName,
    DeviceType DeviceType,
    Guid? DerivedFromSessionId = null);

/// <summary>
/// Repository surface the session service needs from the Infrastructure
/// layer. Kept small so it can be mocked in unit tests.
/// </summary>
public interface ISessionRepository
{
    Task AddAsync(UserSession session, CancellationToken ct);
    Task<UserSession?> FindAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<UserSession>> ListActiveForUserAsync(Guid userId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Hot-path "is this session still active?" probe. Backed by Redis in
/// production (invalidated on revoke), or an in-memory dictionary in
/// tests. Missing keys fall through to the repository.
/// </summary>
public interface ISessionActivityCache
{
    Task<bool?> TryGetActiveAsync(Guid sessionId, CancellationToken ct);
    Task SetActiveAsync(Guid sessionId, bool isActive, TimeSpan ttl, CancellationToken ct);
    Task InvalidateAsync(Guid sessionId, CancellationToken ct);
}

public sealed class SessionService : ISessionService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    private readonly ISessionRepository _repository;
    private readonly ISessionActivityCache _cache;

    public SessionService(ISessionRepository repository, ISessionActivityCache cache)
    {
        _repository = repository;
        _cache = cache;
    }

    public async Task<UserSession> CreateAsync(CreateSessionInput input, CancellationToken ct = default)
    {
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = input.UserId,
            DeviceName = input.DeviceName,
            DeviceType = input.DeviceType,
            IpAddress = input.IpAddress,
            UserAgent = input.UserAgent,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            DerivedFromSessionId = input.DerivedFromSessionId,
        };
        await _repository.AddAsync(session, ct).ConfigureAwait(false);
        await _repository.SaveChangesAsync(ct).ConfigureAwait(false);
        await _cache.SetActiveAsync(session.Id, true, CacheTtl, ct).ConfigureAwait(false);
        return session;
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _repository.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || !session.IsActive) return;
        session.Revoke();
        await _repository.SaveChangesAsync(ct).ConfigureAwait(false);
        await _cache.InvalidateAsync(sessionId, ct).ConfigureAwait(false);
    }

    public async Task RevokeAllForUserAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct = default)
    {
        var sessions = await _repository.ListActiveForUserAsync(userId, ct).ConfigureAwait(false);
        foreach (var s in sessions)
        {
            if (exceptSessionId.HasValue && s.Id == exceptSessionId.Value) continue;
            s.Revoke();
            await _cache.InvalidateAsync(s.Id, ct).ConfigureAwait(false);
        }
        await _repository.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        var cached = await _cache.TryGetActiveAsync(sessionId, ct).ConfigureAwait(false);
        if (cached is not null) return cached.Value;

        var session = await _repository.FindAsync(sessionId, ct).ConfigureAwait(false);
        var active = session?.IsActive ?? false;
        await _cache.SetActiveAsync(sessionId, active, CacheTtl, ct).ConfigureAwait(false);
        return active;
    }

    public async Task TouchAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _repository.FindAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || !session.IsActive) return;
        session.Touch();
        await _repository.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<UserSession>> ListActiveSessionsAsync(Guid userId, CancellationToken ct = default)
        => _repository.ListActiveForUserAsync(userId, ct);
}
