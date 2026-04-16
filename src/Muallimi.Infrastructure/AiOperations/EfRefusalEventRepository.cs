using Microsoft.EntityFrameworkCore;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.AiOperations;

/// <summary>
/// EF-backed repository for <see cref="RefusalEvent"/>. Thin wrapper over
/// <see cref="MuallimiDbContext"/>; kept in Infrastructure so Application
/// tests can substitute an in-memory fake without pulling pgvector in.
/// </summary>
public class EfRefusalEventRepository : IRefusalEventRepository
{
    private readonly MuallimiDbContext _db;

    public EfRefusalEventRepository(MuallimiDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<bool> ExistsAsync(Guid eventId, CancellationToken ct = default)
        => _db.RefusalEvents.AsNoTracking().AnyAsync(r => r.EventId == eventId, ct);

    public Task AddAsync(RefusalEvent row, CancellationToken ct = default)
    {
        _db.RefusalEvents.Add(row);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
