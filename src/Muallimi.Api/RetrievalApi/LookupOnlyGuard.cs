using Muallimi.Infrastructure.Persistence;
using Muallimi.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Muallimi.Api.RetrievalApi;

/// <summary>
/// T098 - Asserts that retrieval handlers never invoke generation paths.
/// Tracks whether any generation-related write occurred during a retrieval request.
/// Used as a post-request assertion in the retrieval pipeline.
/// </summary>
public class LookupOnlyGuard
{
    private readonly MuallimiDbContext _db;
    private int _generationJobCountBefore;
    private int _generatedAssetCountBefore;

    public LookupOnlyGuard(MuallimiDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Snapshot generation state before handling a retrieval request.
    /// </summary>
    public async Task SnapshotBeforeAsync(CancellationToken ct = default)
    {
        _generationJobCountBefore = await _db.GenerationJobs.CountAsync(ct);
        _generatedAssetCountBefore = await _db.GeneratedAssets.CountAsync(ct);
    }

    /// <summary>
    /// Assert that no generation side effects occurred during the retrieval request.
    /// Throws InvalidOperationException if the do-once principle is violated.
    /// </summary>
    public async Task AssertNoGenerationSideEffectsAsync(CancellationToken ct = default)
    {
        var jobCountAfter = await _db.GenerationJobs.CountAsync(ct);
        var assetCountAfter = await _db.GeneratedAssets.CountAsync(ct);

        if (jobCountAfter > _generationJobCountBefore)
        {
            throw new InvalidOperationException(
                $"Lookup-only violation: {jobCountAfter - _generationJobCountBefore} generation job(s) created during retrieval.");
        }

        if (assetCountAfter > _generatedAssetCountBefore)
        {
            throw new InvalidOperationException(
                $"Lookup-only violation: {assetCountAfter - _generatedAssetCountBefore} generated asset(s) created during retrieval.");
        }
    }
}
