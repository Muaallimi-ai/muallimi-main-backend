using Muallimi.Domain.AiOperations;

namespace Muallimi.Application.AiOperations;

/// <summary>
/// Repository boundary for <see cref="RefusalEvent"/> persistence.
/// Introduced so the US2 persistence handler can be unit-tested without
/// requiring the full pgvector-backed DbContext. Infrastructure provides the
/// EF-backed implementation.
/// </summary>
public interface IRefusalEventRepository
{
    Task<bool> ExistsAsync(Guid eventId, CancellationToken ct = default);
    Task AddAsync(RefusalEvent row, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
