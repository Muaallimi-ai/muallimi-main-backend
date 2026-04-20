using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Identity.Adapters;

/// <summary>
/// EF Core-backed <see cref="ISessionRepository"/>. Lives in the
/// Infrastructure project so the Application layer stays free of
/// EF-specific types.
/// </summary>
public sealed class EfSessionRepository : ISessionRepository
{
    private readonly MuallimiDbContext _db;

    public EfSessionRepository(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(UserSession session, CancellationToken ct)
    {
        await _db.IdentityUserSessions.AddAsync(session, ct).ConfigureAwait(false);
    }

    public Task<UserSession?> FindAsync(Guid sessionId, CancellationToken ct)
        => _db.IdentityUserSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);

    public async Task<IReadOnlyList<UserSession>> ListActiveForUserAsync(Guid userId, CancellationToken ct)
    {
        var rows = await _db.IdentityUserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _db.SaveChangesAsync(ct);
}
