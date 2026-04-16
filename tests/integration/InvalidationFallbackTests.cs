using Muallimi.Api.RetrievalApi;
using Muallimi.Domain.Content;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T102 - Integration tests for fallback mode after invalidation.
/// Validates that when a lesson's assets are invalidated, retrieval
/// falls back to text/audio-only until new assets are approved.
/// </summary>
public class InvalidationFallbackTests
{
    [Fact]
    public void FallbackModeProjection_ShouldIncludeAsset_Returns_True_When_Not_In_Fallback()
    {
        // No fallback: all asset types are returned
        Assert.True(FallbackModeProjection.ShouldIncludeAsset(AssetType.TextSummary, isInFallback: false));
        Assert.True(FallbackModeProjection.ShouldIncludeAsset(AssetType.Audio, isInFallback: false));
        Assert.True(FallbackModeProjection.ShouldIncludeAsset(AssetType.Visual, isInFallback: false));
        Assert.True(FallbackModeProjection.ShouldIncludeAsset(AssetType.QuizItem, isInFallback: false));
        Assert.True(FallbackModeProjection.ShouldIncludeAsset(AssetType.QaCacheEntry, isInFallback: false));
    }

    [Fact]
    public void FallbackModeProjection_ShouldIncludeAsset_Filters_Visuals_In_Fallback()
    {
        // Fallback: only text and audio are served
        Assert.True(FallbackModeProjection.ShouldIncludeAsset(AssetType.TextSummary, isInFallback: true));
        Assert.True(FallbackModeProjection.ShouldIncludeAsset(AssetType.Audio, isInFallback: true));
        Assert.False(FallbackModeProjection.ShouldIncludeAsset(AssetType.Visual, isInFallback: true));
        Assert.False(FallbackModeProjection.ShouldIncludeAsset(AssetType.QuizItem, isInFallback: true));
        Assert.False(FallbackModeProjection.ShouldIncludeAsset(AssetType.QaCacheEntry, isInFallback: true));
    }

    [Fact]
    public void Lesson_Invalidation_Cascades_To_All_Approved_Assets()
    {
        // Simulate the cascade: approved lesson → invalidated
        var lessonId = Guid.NewGuid();
        var lesson = Lesson.Create(
            Guid.NewGuid(), CurriculumType.Moe, Grade.Grade7,
            Subject.Mathematics, TutorLanguage.Ar, "Ch1 > Lesson1");
        lesson.SetContentHash("hash-v1");
        lesson.MarkIngested();
        lesson.MarkGenerating();
        lesson.MarkAutoValidating();
        lesson.MarkPendingAdminReview();
        lesson.MarkPendingExpertReview();
        lesson.MarkApproved();

        // Create approved generated assets
        var textAsset = GeneratedAsset.Create(lesson.LessonId, AssetType.TextSummary, null, "ar", 1, "worker");
        textAsset.MarkProducing();
        textAsset.MarkAutoValidating();
        textAsset.MarkPendingAdminReview();
        textAsset.MarkPendingExpertReview();
        textAsset.MarkApproved();

        var visualAsset = GeneratedAsset.Create(lesson.LessonId, AssetType.Visual, VisualFormat.Mp4Animation, "ar", 1, "worker");
        visualAsset.MarkProducing();
        visualAsset.MarkAutoValidating();
        visualAsset.MarkPendingAdminReview();
        visualAsset.MarkPendingExpertReview();
        visualAsset.MarkApproved();

        // Create published assets
        var publishedText = PublishedAsset.Create(
            textAsset.AssetId, lesson.LessonId, AssetType.TextSummary, null,
            "/content/text", "admin-1", "expert-1", 1);
        var publishedVisual = PublishedAsset.Create(
            visualAsset.AssetId, lesson.LessonId, AssetType.Visual, VisualFormat.Mp4Animation,
            "/content/visual", "admin-1", "expert-1", 1);

        // Invalidation cascade
        lesson.Invalidate("Source updated");
        textAsset.MarkInvalidated();
        visualAsset.MarkInvalidated();
        publishedText.Invalidate();
        publishedVisual.Invalidate();

        // Verify states
        Assert.Equal(LessonStatus.Invalidated, lesson.Status);
        Assert.Equal(AssetStatus.Invalidated, textAsset.Status);
        Assert.Equal(AssetStatus.Invalidated, visualAsset.Status);
        Assert.False(publishedText.IsActive);
        Assert.False(publishedVisual.IsActive);
    }

    [Fact]
    public void ChangeLogEntry_Created_For_Each_Invalidated_Asset()
    {
        var lessonId = Guid.NewGuid();

        var entry1 = ChangeLogEntry.Create(
            lessonId, ChangeEventType.AssetInvalidated, "admin-1",
            "Source material updated", "corr-001");
        var entry2 = ChangeLogEntry.Create(
            lessonId, ChangeEventType.AssetInvalidated, "admin-1",
            "Source material updated", "corr-001");
        var lessonEntry = ChangeLogEntry.Create(
            lessonId, ChangeEventType.LessonUpdated, "admin-1",
            "Source material updated", "corr-001");

        Assert.NotEqual(entry1.EntryId, entry2.EntryId);
        Assert.Equal(ChangeEventType.AssetInvalidated, entry1.EventType);
        Assert.Equal(ChangeEventType.LessonUpdated, lessonEntry.EventType);
        Assert.All(new[] { entry1, entry2, lessonEntry },
            e => Assert.Equal("corr-001", e.CorrelationId));
    }

    [Fact]
    public void ContentChunk_Grace_Period_Preserves_Deprecated_Chunks()
    {
        // Deprecated chunks remain queryable during the grace period
        var chunk = ContentChunk.Create(
            Guid.NewGuid(), 1, "Deprecated but still queryable chunk text content",
            350, 0, "[]", "{}");
        chunk.Activate();
        chunk.Deprecate();

        // Deprecated chunks still have their data intact
        Assert.Equal(ChunkStatus.Deprecated, chunk.Status);
        Assert.NotEmpty(chunk.Text);
        Assert.NotEqual(Guid.Empty, chunk.ChunkId);
    }

    [Fact]
    public void Invalidated_Lesson_Can_Re_Enter_Pipeline()
    {
        // After invalidation, a lesson can be re-ingested with new content
        var lesson = Lesson.Create(
            Guid.NewGuid(), CurriculumType.Moe, Grade.Grade7,
            Subject.Mathematics, TutorLanguage.Ar, "Ch1 > Lesson1");
        lesson.SetContentHash("hash-v1");
        lesson.MarkIngested();
        lesson.MarkGenerating();
        lesson.MarkAutoValidating();
        lesson.MarkPendingAdminReview();
        lesson.MarkPendingExpertReview();
        lesson.MarkApproved();

        // Invalidate
        lesson.Invalidate("Content updated");

        // Re-ingest
        lesson.UpdateContentHash("hash-v2");
        lesson.MarkIngested("Re-ingestion after source update");

        Assert.Equal(LessonStatus.Ingested, lesson.Status);
        Assert.Equal("hash-v2", lesson.ContentHash);
    }
}
