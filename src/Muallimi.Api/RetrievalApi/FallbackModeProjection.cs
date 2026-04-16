using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.RetrievalApi;

/// <summary>
/// Projects whether a lesson is in fallback mode: when a lesson has been invalidated
/// and its assets are being regenerated, retrieval returns text/audio-only
/// (visuals and quiz items are suppressed until new assets are approved).
/// </summary>
public class FallbackModeProjection
{
    private readonly MuallimiDbContext _db;

    public FallbackModeProjection(MuallimiDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns fallback info for a lesson: which asset types are currently available.
    /// A lesson is in fallback mode if it was previously approved but is now invalidated.
    /// </summary>
    public async Task<LessonFallbackInfo> GetFallbackInfoAsync(Guid lessonId)
    {
        var lesson = await _db.Lessons.FindAsync(lessonId);
        if (lesson is null)
            return new LessonFallbackInfo(lessonId, false, []);

        // A lesson is in fallback mode when it's invalidated (was approved, now re-processing)
        var isInFallback = lesson.Status == LessonStatus.Invalidated;

        if (!isInFallback)
            return new LessonFallbackInfo(lessonId, false, []);

        // In fallback mode, check which asset types still have active published versions
        // Text and audio are safe mode; visuals, quiz, and Q&A cache are suppressed
        var activePublished = await _db.PublishedAssets
            .Where(p => p.LessonId == lessonId && p.Status == PublishedAssetStatus.Active)
            .Select(p => p.AssetType)
            .Distinct()
            .ToListAsync();

        // Safe asset types that can still be served during fallback
        var safeTypes = new HashSet<AssetType> { AssetType.TextSummary, AssetType.Audio };
        var availableTypes = activePublished.Where(t => safeTypes.Contains(t)).ToList();

        return new LessonFallbackInfo(lessonId, true, availableTypes);
    }

    /// <summary>
    /// Checks multiple lessons and returns which ones are in fallback mode.
    /// Used by the retrieval endpoint to filter visual assets from response.
    /// </summary>
    public async Task<Dictionary<Guid, bool>> GetFallbackStatusBatchAsync(IEnumerable<Guid> lessonIds)
    {
        var ids = lessonIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, bool>();

        var invalidatedLessons = await _db.Lessons
            .Where(l => ids.Contains(l.LessonId) && l.Status == LessonStatus.Invalidated)
            .Select(l => l.LessonId)
            .ToListAsync();

        return ids.ToDictionary(id => id, id => invalidatedLessons.Contains(id));
    }

    /// <summary>
    /// Filters retrieval results to suppress visual/quiz/Q&A cache assets for lessons in fallback mode.
    /// Returns true if the asset type should be included in retrieval results for this lesson.
    /// </summary>
    public static bool ShouldIncludeAsset(AssetType assetType, bool isInFallback)
    {
        if (!isInFallback) return true;

        // In fallback mode, only text and audio are served
        return assetType == AssetType.TextSummary || assetType == AssetType.Audio;
    }
}

public record LessonFallbackInfo(
    Guid LessonId,
    bool IsInFallbackMode,
    List<AssetType> AvailableAssetTypes);
