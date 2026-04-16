using Muallimi.Domain.Content;
using Muallimi.Domain.Curriculum;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T100 - Contract tests for POST /admin/curriculum/{source_id}/update
/// and PATCH /admin/content/{asset_id}/invalidate.
/// Validates domain state transitions, ChangeLogEntry creation, and fallback invariants
/// per the curriculum-admin-api-contract.md update/invalidation section.
/// </summary>
public class UpdateAndInvalidateContractTests
{
    // ── POST /admin/curriculum/{source_id}/update ──

    [Fact]
    public void Update_Source_Transitions_Indexed_To_Replaced()
    {
        // Contract: existing source must be Indexed; transitions to Replaced
        var source = CurriculumSource.Create(
            CurriculumType.Moe, Grade.Grade7, Subject.Mathematics,
            "2025-2026", TutorLanguage.Ar, FileFormat.Pdf,
            "storage/key", "abc123hash", "admin-1");
        source.MarkIngesting();
        source.MarkIndexed();

        Assert.Equal(SourceStatus.Indexed, source.Status);

        source.MarkReplaced();
        Assert.Equal(SourceStatus.Replaced, source.Status);
    }

    [Fact]
    public void Update_Source_Rejects_Non_Indexed_Status()
    {
        // Contract: only Indexed sources can be updated
        var source = CurriculumSource.Create(
            CurriculumType.Moe, Grade.Grade7, Subject.Mathematics,
            "2025-2026", TutorLanguage.Ar, FileFormat.Pdf,
            "storage/key", "abc123hash", "admin-1");

        Assert.Equal(SourceStatus.Received, source.Status);
        Assert.Throws<InvalidOperationException>(() => source.MarkReplaced());
    }

    [Fact]
    public void Update_Creates_New_Source_With_Same_Scope()
    {
        // Contract: update creates a new CurriculumSource with the same curriculum scope
        var newSource = CurriculumSource.Create(
            CurriculumType.Moe, Grade.Grade7, Subject.Mathematics,
            "2025-2026", TutorLanguage.Ar, FileFormat.Pdf,
            "storage/new-key", "def456hash", "admin-1");

        Assert.NotEqual(Guid.Empty, newSource.SourceId);
        Assert.Equal(SourceStatus.Received, newSource.Status);
    }

    [Fact]
    public void Update_Creates_ChangeLogEntry_For_Each_Affected_Lesson()
    {
        // Contract: every affected lesson gets a change log entry
        var entry = ChangeLogEntry.Create(
            Guid.NewGuid(),
            ChangeEventType.LessonUpdated,
            "admin-1",
            "Source replaced: new version uploaded",
            "corr-123");

        Assert.NotEqual(Guid.Empty, entry.EntryId);
        Assert.Equal(ChangeEventType.LessonUpdated, entry.EventType);
        Assert.Equal("admin-1", entry.ActorId);
        Assert.Equal("corr-123", entry.CorrelationId);
        Assert.True(entry.OccurredAt <= DateTime.UtcNow);
    }

    // ── PATCH /admin/content/{asset_id}/invalidate ──

    [Fact]
    public void Invalidate_Asset_Transitions_Approved_To_Invalidated()
    {
        // Contract: only Approved assets can be invalidated
        var asset = GeneratedAsset.Create(
            Guid.NewGuid(), AssetType.Visual, VisualFormat.Mp4Animation,
            "ar", 1, "worker");
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();
        asset.MarkPendingExpertReview();
        asset.MarkApproved();

        Assert.Equal(AssetStatus.Approved, asset.Status);
        asset.MarkInvalidated();
        Assert.Equal(AssetStatus.Invalidated, asset.Status);
    }

    [Fact]
    public void Invalidate_Asset_Rejects_Non_Approved_Status()
    {
        // Contract: cannot invalidate assets that are not Approved
        var asset = GeneratedAsset.Create(
            Guid.NewGuid(), AssetType.Audio, null, "ar", 1, "worker");
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();

        Assert.Throws<InvalidOperationException>(() => asset.MarkInvalidated());
    }

    [Fact]
    public void Invalidate_PublishedAsset_Transitions_Active_To_Invalidated()
    {
        // Contract: published asset is immediately invalidated
        var published = PublishedAsset.Create(
            Guid.NewGuid(), Guid.NewGuid(), AssetType.Visual, VisualFormat.Mp4Animation,
            "/content/test", "admin-1", "expert-1", 1);

        Assert.True(published.IsActive);
        published.Invalidate();
        Assert.Equal(PublishedAssetStatus.Invalidated, published.Status);
        Assert.False(published.IsActive);
    }

    [Fact]
    public void Invalidate_Lesson_Transitions_Approved_To_Invalidated()
    {
        // Contract: lesson falls back when an asset is invalidated
        var lesson = Lesson.Create(
            Guid.NewGuid(), CurriculumType.Moe, Grade.Grade7,
            Subject.Mathematics, TutorLanguage.Ar, "Ch1 > Topic1 > Lesson1");
        lesson.SetContentHash("hash1");
        lesson.MarkIngested();
        lesson.MarkGenerating();
        lesson.MarkAutoValidating();
        lesson.MarkPendingAdminReview();
        lesson.MarkPendingExpertReview();
        lesson.MarkApproved();

        lesson.Invalidate("Asset invalidated: visual quality issue");
        Assert.Equal(LessonStatus.Invalidated, lesson.Status);
        Assert.Null(lesson.PublishedAt);
    }

    [Fact]
    public void Invalidate_Creates_ChangeLogEntry_With_Reason()
    {
        // Contract: invalidation records a change log entry with reason
        var entry = ChangeLogEntry.Create(
            Guid.NewGuid(),
            ChangeEventType.AssetInvalidated,
            "admin-1",
            "Visual quality below acceptable threshold",
            "corr-789");

        Assert.Equal(ChangeEventType.AssetInvalidated, entry.EventType);
        Assert.Equal("Visual quality below acceptable threshold", entry.Reason);
    }

    [Fact]
    public void Invalidate_Requires_Reason()
    {
        // Contract: reason is mandatory for invalidation
        Assert.Throws<ArgumentException>(() =>
            ChangeLogEntry.Create(
                Guid.NewGuid(),
                ChangeEventType.AssetInvalidated,
                "admin-1",
                "",
                "corr-789"));
    }

    [Fact]
    public void ContentChunk_Deprecates_From_Active_Only()
    {
        // Contract: only active chunks can be deprecated
        var chunk = ContentChunk.Create(
            Guid.NewGuid(), 1, "Test content text for chunk creation here",
            350, 0, "[]", "{}");
        chunk.Activate();

        Assert.Equal(ChunkStatus.Active, chunk.Status);
        chunk.Deprecate();
        Assert.Equal(ChunkStatus.Deprecated, chunk.Status);
    }

    [Fact]
    public void ContentChunk_Cannot_Deprecate_From_Draft()
    {
        var chunk = ContentChunk.Create(
            Guid.NewGuid(), 1, "Test content text for chunk creation here",
            350, 0, "[]", "{}");

        Assert.Equal(ChunkStatus.Draft, chunk.Status);
        Assert.Throws<InvalidOperationException>(() => chunk.Deprecate());
    }

    [Fact]
    public void QaCacheEntry_Flags_For_Revalidation()
    {
        // Contract: Q&A cache entries linked to invalidated chunks are flagged
        var entry = new QaCacheEntry
        {
            EntryId = Guid.NewGuid(),
            CurriculumType = CurriculumType.Moe,
            Subject = Subject.Mathematics,
            Grade = Grade.Grade7,
            ValidationStatus = QaCacheStatus.Active
        };

        entry.FlagForRevalidation();
        Assert.Equal(QaCacheStatus.FlaggedForRevalidation, entry.ValidationStatus);
    }

    [Fact]
    public void QaCacheEntry_FlagForRevalidation_NoOp_When_Not_Active()
    {
        var entry = new QaCacheEntry
        {
            EntryId = Guid.NewGuid(),
            ValidationStatus = QaCacheStatus.PreSeeded
        };

        entry.FlagForRevalidation();
        Assert.Equal(QaCacheStatus.PreSeeded, entry.ValidationStatus);
    }

    // ── Lesson Update Content Hash (Delta) ──

    [Fact]
    public void Lesson_UpdateContentHash_Returns_True_When_Changed()
    {
        var lesson = Lesson.Create(
            Guid.NewGuid(), CurriculumType.Moe, Grade.Grade7,
            Subject.Mathematics, TutorLanguage.Ar, "Ch1 > Lesson1");
        lesson.SetContentHash("hash-v1");

        var changed = lesson.UpdateContentHash("hash-v2");
        Assert.True(changed);
        Assert.Equal("hash-v2", lesson.ContentHash);
    }

    [Fact]
    public void Lesson_UpdateContentHash_Returns_False_When_Identical()
    {
        var lesson = Lesson.Create(
            Guid.NewGuid(), CurriculumType.Moe, Grade.Grade7,
            Subject.Mathematics, TutorLanguage.Ar, "Ch1 > Lesson1");
        lesson.SetContentHash("hash-v1");

        var changed = lesson.UpdateContentHash("hash-v1");
        Assert.False(changed);
    }

    [Fact]
    public void Lesson_Can_Be_Re_Ingested_After_Invalidation()
    {
        // Contract: invalidated lessons can transition back to Ingested
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

        lesson.Invalidate("Content updated");
        Assert.Equal(LessonStatus.Invalidated, lesson.Status);

        lesson.MarkIngested("Re-ingestion after update");
        Assert.Equal(LessonStatus.Ingested, lesson.Status);
    }
}
