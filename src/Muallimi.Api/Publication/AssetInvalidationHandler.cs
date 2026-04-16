using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Publication;

/// <summary>
/// Invalidates all published assets for a lesson, transitions GeneratedAssets to Invalidated,
/// deprecates active chunks, writes ChangeLogEntry records, and resets the lesson to Invalidated status
/// so it can be re-ingested. Used by both automatic re-processing (curriculum update) and manual invalidation.
/// </summary>
public class AssetInvalidationHandler
{
    private readonly MuallimiDbContext _db;

    public AssetInvalidationHandler(MuallimiDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Invalidates all active published assets and approved generated assets for a lesson.
    /// Deprecates active content chunks. Writes ChangeLogEntry for each invalidated asset.
    /// Transitions the lesson to Invalidated status.
    /// </summary>
    /// <returns>Count of invalidated assets.</returns>
    public async Task<InvalidationResult> InvalidateLessonAssetsAsync(
        Guid lessonId,
        string actorId,
        string reason,
        string? correlationId)
    {
        var lesson = await _db.Lessons.FindAsync(lessonId);
        if (lesson is null)
            throw new InvalidOperationException($"Lesson {lessonId} not found.");

        int publishedCount = 0;
        int generatedCount = 0;
        int chunksDeprecated = 0;

        // 1. Invalidate all active PublishedAssets for this lesson
        var publishedAssets = await _db.PublishedAssets
            .Where(p => p.LessonId == lessonId && p.Status == PublishedAssetStatus.Active)
            .ToListAsync();

        foreach (var published in publishedAssets)
        {
            published.Invalidate();
            publishedCount++;

            _db.ChangeLogEntries.Add(ChangeLogEntry.Create(
                lessonId,
                ChangeEventType.AssetInvalidated,
                actorId,
                reason,
                correlationId));
        }

        // 2. Invalidate all Approved GeneratedAssets for this lesson
        var generatedAssets = await _db.GeneratedAssets
            .Where(a => a.LessonId == lessonId && a.Status == AssetStatus.Approved)
            .ToListAsync();

        foreach (var asset in generatedAssets)
        {
            asset.MarkInvalidated();
            generatedCount++;
        }

        // 3. Deprecate active chunks (they remain queryable during grace period)
        var activeChunks = await _db.ContentChunks
            .Where(c => c.LessonId == lessonId && c.Status == ChunkStatus.Active)
            .ToListAsync();

        foreach (var chunk in activeChunks)
        {
            chunk.Deprecate();
            chunksDeprecated++;
        }

        // 4. Update CoverageStatus entries for this lesson
        var coverages = await _db.CoverageStatuses
            .Where(c => c.LessonId == lessonId)
            .ToListAsync();

        foreach (var coverage in coverages)
        {
            coverage.State = CoverageState.NotStarted;
            coverage.LastUpdatedAt = DateTime.UtcNow;
        }

        // 5. Transition lesson to Invalidated (only from Approved)
        if (lesson.Status == LessonStatus.Approved)
        {
            lesson.Invalidate(reason);

            _db.ChangeLogEntries.Add(ChangeLogEntry.Create(
                lessonId,
                ChangeEventType.LessonUpdated,
                actorId,
                reason,
                correlationId));
        }

        // 6. Flag Q&A cache entries for revalidation
        var qaCacheEntries = await _db.QaCacheEntries
            .Where(q => q.Subject == lesson.Subject
                        && q.CurriculumType == lesson.CurriculumType
                        && q.Grade == lesson.Grade)
            .Where(q => q.ValidationStatus == QaCacheStatus.Active)
            .ToListAsync();

        foreach (var entry in qaCacheEntries)
        {
            entry.FlagForRevalidation();
        }

        await _db.SaveChangesAsync();

        return new InvalidationResult(publishedCount, generatedCount, chunksDeprecated);
    }

    /// <summary>
    /// Invalidates a single published asset by its asset ID (for manual invalidation).
    /// </summary>
    public async Task<SingleAssetInvalidationResult> InvalidateSingleAssetAsync(
        Guid assetId,
        string actorId,
        string reason,
        string? correlationId)
    {
        var generatedAsset = await _db.GeneratedAssets.FindAsync(assetId);
        if (generatedAsset is null)
            throw new InvalidOperationException($"Asset {assetId} not found.");

        if (generatedAsset.Status != AssetStatus.Approved)
            throw new InvalidOperationException($"Cannot invalidate asset in status '{generatedAsset.Status}'. Must be Approved.");

        generatedAsset.MarkInvalidated();

        // Also invalidate the published copy
        var published = await _db.PublishedAssets.FindAsync(assetId);
        if (published is not null && published.Status == PublishedAssetStatus.Active)
        {
            published.Invalidate();
        }

        _db.ChangeLogEntries.Add(ChangeLogEntry.Create(
            generatedAsset.LessonId,
            ChangeEventType.AssetInvalidated,
            actorId,
            reason,
            correlationId));

        // Update coverage status
        var coverage = await _db.CoverageStatuses
            .FindAsync(generatedAsset.LessonId, generatedAsset.AssetType);
        if (coverage is not null)
        {
            coverage.State = CoverageState.NotStarted;
            coverage.LastUpdatedAt = DateTime.UtcNow;
        }

        // Check if the lesson should fall back — if any required asset is now invalidated
        var lesson = await _db.Lessons.FindAsync(generatedAsset.LessonId);
        if (lesson is not null && lesson.Status == LessonStatus.Approved)
        {
            lesson.Invalidate($"Asset {assetId} invalidated: {reason}");

            _db.ChangeLogEntries.Add(ChangeLogEntry.Create(
                generatedAsset.LessonId,
                ChangeEventType.LessonUpdated,
                actorId,
                $"Lesson invalidated due to asset {assetId} invalidation",
                correlationId));
        }

        await _db.SaveChangesAsync();

        return new SingleAssetInvalidationResult(
            generatedAsset.LessonId,
            assetId,
            generatedAsset.AssetType.ToString(),
            lesson?.Status.ToString() ?? "unknown");
    }
}

public record InvalidationResult(int PublishedAssetsInvalidated, int GeneratedAssetsInvalidated, int ChunksDeprecated);
public record SingleAssetInvalidationResult(Guid LessonId, Guid AssetId, string AssetType, string LessonStatus);
