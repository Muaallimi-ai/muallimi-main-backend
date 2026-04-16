using Muallimi.Domain.Content;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T069 - Integration tests for reject/edit paths and concurrent approval conflict.
/// Verifies rejected assets return to pipeline and double-approve is blocked.
/// </summary>
public class ReviewRejectionTests
{
    private static GeneratedAsset CreateAssetAtExpertReview()
    {
        var asset = GeneratedAsset.Create(Guid.NewGuid(), AssetType.TextSummary, null, "ar", 1, "worker");
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();
        asset.MarkPendingExpertReview();
        return asset;
    }

    [Fact]
    public void Rejection_Returns_Asset_To_Pipeline_With_FixInstruction()
    {
        var asset = CreateAssetAtExpertReview();

        // Expert rejects
        asset.MarkRejected();
        Assert.Equal(AssetStatus.Rejected, asset.Status);

        // Record rejection with fix instruction
        var decision = ReviewDecision.CreateExpertDecision(
            asset.AssetId, ReviewOutcome.Rejected, "expert-1",
            "The narration pace is too fast for Grade 7 students", null, "corr-reject-1");

        Assert.Equal(ReviewOutcome.Rejected, decision.Outcome);
        Assert.Equal("The narration pace is too fast for Grade 7 students", decision.FixInstruction);

        // Asset returns to queue for regeneration
        asset.ResetForRegeneration(2);
        Assert.Equal(AssetStatus.Queued, asset.Status);
        Assert.Equal(2, asset.Version);
    }

    [Fact]
    public void EditRequest_Returns_Asset_With_Stage_Scope()
    {
        var asset = CreateAssetAtExpertReview();

        // Expert requests edit scoped to narration stage
        asset.MarkEditRequested();
        Assert.Equal(AssetStatus.EditRequested, asset.Status);

        var decision = ReviewDecision.CreateExpertDecision(
            asset.AssetId, ReviewOutcome.EditRequested, "expert-1",
            "Narration needs to be re-recorded with clearer diacritics",
            "narration", "corr-edit-1");

        Assert.Equal(ReviewOutcome.EditRequested, decision.Outcome);
        Assert.Equal("narration", decision.Scope);
        Assert.Equal("Narration needs to be re-recorded with clearer diacritics", decision.FixInstruction);

        // Asset goes back to regeneration
        asset.ResetForRegeneration(2);
        Assert.Equal(AssetStatus.Queued, asset.Status);
    }

    [Fact]
    public void Concurrent_Approval_Second_Attempt_Is_Blocked()
    {
        var asset = CreateAssetAtExpertReview();

        // First expert approves
        asset.MarkApproved();
        Assert.Equal(AssetStatus.Approved, asset.Status);

        // Second expert cannot approve (asset no longer in PendingExpertReview)
        Assert.Throws<InvalidOperationException>(() => asset.MarkApproved());
    }

    [Fact]
    public void Rejected_Asset_Must_Go_Through_Full_Review_Cycle_Again()
    {
        var asset = CreateAssetAtExpertReview();

        // Reject -> regenerate
        asset.MarkRejected();
        asset.ResetForRegeneration(2);
        Assert.Equal(AssetStatus.Queued, asset.Status);

        // Cannot jump to approved from queued
        Assert.Throws<InvalidOperationException>(() => asset.MarkApproved());

        // Must go through full cycle
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();
        asset.MarkPendingExpertReview();
        asset.MarkApproved();
        Assert.Equal(AssetStatus.Approved, asset.Status);
    }

    [Fact]
    public void Admin_Can_Regenerate_From_PendingAdminReview()
    {
        var asset = GeneratedAsset.Create(Guid.NewGuid(), AssetType.Audio, null, "ar", 1, "worker");
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();

        // Admin sends back for regeneration — this is equivalent to rejection at Tier 2
        // Admin review doesn't have reject as a formal status, but they can regenerate
        // which the API handles by calling ResetForRegeneration
        // We need to mark rejected first (from PendingAdminReview, this should fail)
        // Actually per the state machine: PendingAdminReview -> Queued is allowed
        // Let's verify it goes through the proper rejection
        asset.MarkRejected();
        asset.ResetForRegeneration(2);

        Assert.Equal(AssetStatus.Queued, asset.Status);
        Assert.Equal(2, asset.Version);
    }

    [Fact]
    public void AutoFailed_Asset_Goes_Through_Regeneration()
    {
        var asset = GeneratedAsset.Create(Guid.NewGuid(), AssetType.Visual, VisualFormat.Diagram, "ar", 1, "worker");
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkAutoFailed();

        Assert.Equal(AssetStatus.AutoFailed, asset.Status);

        // Cannot go directly to admin review
        Assert.Throws<InvalidOperationException>(() => asset.MarkPendingAdminReview());

        // Must regenerate first
        asset.ResetForRegeneration(2);
        Assert.Equal(AssetStatus.Queued, asset.Status);

        // Then go through full cycle
        asset.MarkProducing();
        asset.MarkAutoValidating();
        asset.MarkPendingAdminReview();
        Assert.Equal(AssetStatus.PendingAdminReview, asset.Status);
    }

    [Fact]
    public void Multiple_Decisions_Form_Complete_Audit_Trail()
    {
        var assetId = Guid.NewGuid();

        // First round: auto-validation pass, admin submit, expert reject
        var d1 = ReviewDecision.CreateAutoValidation(assetId, ReviewOutcome.Approved, "corr-1");
        var d2 = ReviewDecision.CreateAdminDecision(assetId, ReviewOutcome.Approved, "admin-1", "corr-1");
        var d3 = ReviewDecision.CreateExpertDecision(
            assetId, ReviewOutcome.Rejected, "expert-1",
            "Incorrect mathematical formula", null, "corr-1");

        // Second round after regeneration: all approve
        var d4 = ReviewDecision.CreateAutoValidation(assetId, ReviewOutcome.Approved, "corr-2");
        var d5 = ReviewDecision.CreateAdminDecision(assetId, ReviewOutcome.Approved, "admin-1", "corr-2");
        var d6 = ReviewDecision.CreateExpertDecision(
            assetId, ReviewOutcome.Approved, "expert-1", null, null, "corr-2");

        var decisions = new[] { d1, d2, d3, d4, d5, d6 };

        // All have unique decision IDs
        Assert.Equal(6, decisions.Select(d => d.DecisionId).Distinct().Count());

        // Rejection has fix instruction
        Assert.NotNull(d3.FixInstruction);
        Assert.Equal("Incorrect mathematical formula", d3.FixInstruction);

        // Approval has no fix instruction
        Assert.Null(d6.FixInstruction);
    }
}
