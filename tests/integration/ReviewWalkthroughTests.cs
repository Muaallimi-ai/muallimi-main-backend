using Muallimi.Domain.Content;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T068 - Integration test for the full review walkthrough:
/// auto-validation pass -> admin submit -> expert approve -> PublishedAsset active.
/// Verifies the complete happy path through all three review tiers.
/// </summary>
public class ReviewWalkthroughTests
{
    [Fact]
    public void Full_Review_Walkthrough_From_AutoValidation_To_Published()
    {
        // 1. Create a generated asset (starting from queued)
        var lessonId = Guid.NewGuid();
        var asset = GeneratedAsset.Create(lessonId, AssetType.TextSummary, null, "ar", 1, "generation-worker");
        Assert.Equal(AssetStatus.Queued, asset.Status);

        // 2. Generation completes
        asset.MarkProducing();
        Assert.Equal(AssetStatus.Producing, asset.Status);

        // 3. Auto-validation begins
        asset.MarkAutoValidating();
        Assert.Equal(AssetStatus.AutoValidating, asset.Status);

        // 4. Auto-validation passes -> pending admin review
        asset.MarkPendingAdminReview();
        Assert.Equal(AssetStatus.PendingAdminReview, asset.Status);

        // Record auto-validation decision
        var autoDecision = ReviewDecision.CreateAutoValidation(
            asset.AssetId, ReviewOutcome.Approved, "corr-walkthrough-1");
        Assert.Equal(ReviewTier.AutoValidation, autoDecision.Tier);

        // 5. Create auto-validation result
        var validationResult = AutoValidationResult.Create(
            asset.AssetId,
            "{\"grounding\":{\"status\":\"passed\"},\"arabic_language_quality\":{\"status\":\"passed\"}}",
            "[{\"source_chunk_id\":\"chunk-1\",\"support_score\":0.92}]",
            "{\"grammar\":{\"status\":\"passed\"}}",
            null, null, null, null,
            AutoValidationDecision.Passed);
        Assert.True(validationResult.Passed);

        // 6. Admin submits to expert review
        asset.MarkPendingExpertReview();
        Assert.Equal(AssetStatus.PendingExpertReview, asset.Status);

        var adminDecision = ReviewDecision.CreateAdminDecision(
            asset.AssetId, ReviewOutcome.Approved, "admin-1", "corr-walkthrough-1");
        Assert.Equal(ReviewTier.AdminReview, adminDecision.Tier);

        // 7. Expert approves
        asset.MarkApproved();
        Assert.Equal(AssetStatus.Approved, asset.Status);

        var expertDecision = ReviewDecision.CreateExpertDecision(
            asset.AssetId, ReviewOutcome.Approved, "expert-1",
            null, null, "corr-walkthrough-1");
        Assert.Equal(ReviewTier.ExpertReview, expertDecision.Tier);

        // 8. Create PublishedAsset
        var runtimeUrl = $"/content/{lessonId}/textsummary/{asset.AssetId}";
        var published = PublishedAsset.Create(
            asset.AssetId, lessonId, AssetType.TextSummary, null,
            runtimeUrl, "admin-1", "expert-1", 1);

        Assert.True(published.IsActive);
        Assert.Equal(asset.AssetId, published.PublishedId);
        Assert.Equal("admin-1", published.ApprovedByAdmin);
        Assert.Equal("expert-1", published.ApprovedByExpert);
    }

    [Fact]
    public void Only_Approved_Assets_Become_Publishable()
    {
        // An asset in PendingExpertReview is NOT publishable
        var asset = GeneratedAsset.Create(Guid.NewGuid(), AssetType.Audio, null, "ar", 1, "worker");
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();
        asset.MarkPendingExpertReview();

        Assert.False(ReviewStateMachine.IsPublishable(asset.Status));

        // After approval, it IS publishable
        asset.MarkApproved();
        Assert.True(ReviewStateMachine.IsPublishable(asset.Status));
    }

    [Fact]
    public void Approval_Records_Complete_Audit_Trail()
    {
        var assetId = Guid.NewGuid();
        var correlationId = "corr-audit-test";

        // Auto-validation decision
        var autoDecision = ReviewDecision.CreateAutoValidation(assetId, ReviewOutcome.Approved, correlationId);
        Assert.Equal("system", autoDecision.ActorId);
        Assert.Equal(correlationId, autoDecision.CorrelationId);

        // Admin decision
        var adminDecision = ReviewDecision.CreateAdminDecision(assetId, ReviewOutcome.Approved, "admin-1", correlationId);
        Assert.Equal("admin-1", adminDecision.ActorId);

        // Expert decision
        var expertDecision = ReviewDecision.CreateExpertDecision(
            assetId, ReviewOutcome.Approved, "expert-1", null, null, correlationId);
        Assert.Equal("expert-1", expertDecision.ActorId);
        Assert.Equal(ReviewTier.ExpertReview, expertDecision.Tier);

        // All three decisions recorded with correlation ID
        Assert.All(new[] { autoDecision, adminDecision, expertDecision },
            d => Assert.Equal(correlationId, d.CorrelationId));
    }

    [Fact]
    public void Visual_Asset_Walkthrough_With_Format()
    {
        var lessonId = Guid.NewGuid();
        var asset = GeneratedAsset.Create(
            lessonId, AssetType.Visual, VisualFormat.Mp4Animation, "ar", 1, "worker");

        // Full walkthrough
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();
        asset.MarkPendingExpertReview();
        asset.MarkApproved();

        var published = PublishedAsset.Create(
            asset.AssetId, lessonId, AssetType.Visual, VisualFormat.Mp4Animation,
            "/content/visual/mp4", "admin-1", "expert-1", 1);

        Assert.Equal(VisualFormat.Mp4Animation, published.VisualFormat);
        Assert.True(published.IsActive);
    }
}
