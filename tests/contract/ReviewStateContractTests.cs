using Muallimi.Domain.Content;
using Muallimi.Domain.Review;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T018/T066 - Contract tests for review state transitions.
/// Validates every allowed and forbidden state transition per review-state-contract.md.
/// </summary>
public class ReviewStateContractTests
{
    private static GeneratedAsset CreateTestAsset(AssetStatus targetStatus = AssetStatus.Queued)
    {
        var asset = GeneratedAsset.Create(
            Guid.NewGuid(), AssetType.TextSummary, null, "ar", 1, "test-worker");

        // Advance to the requested status
        if (targetStatus == AssetStatus.Queued) return asset;
        asset.MarkProducing();
        if (targetStatus == AssetStatus.Producing) return asset;
        asset.MarkAutoValidating();
        if (targetStatus == AssetStatus.AutoValidating) return asset;

        if (targetStatus == AssetStatus.AutoFailed)
        {
            asset.MarkAutoFailed();
            return asset;
        }

        asset.MarkPendingAdminReview();
        if (targetStatus == AssetStatus.PendingAdminReview) return asset;
        asset.MarkPendingExpertReview();
        if (targetStatus == AssetStatus.PendingExpertReview) return asset;

        if (targetStatus == AssetStatus.Approved) { asset.MarkApproved(); return asset; }
        if (targetStatus == AssetStatus.Rejected) { asset.MarkRejected(); return asset; }
        if (targetStatus == AssetStatus.EditRequested) { asset.MarkEditRequested(); return asset; }

        return asset;
    }

    // --- Allowed transitions ---

    [Fact]
    public void Queued_To_Producing_Is_Allowed()
    {
        var asset = CreateTestAsset(AssetStatus.Queued);
        asset.MarkProducing();
        Assert.Equal(AssetStatus.Producing, asset.Status);
    }

    [Fact]
    public void Producing_To_AutoValidating_Is_Allowed()
    {
        var asset = CreateTestAsset(AssetStatus.Producing);
        asset.MarkAutoValidating();
        Assert.Equal(AssetStatus.AutoValidating, asset.Status);
    }

    [Fact]
    public void AutoValidation_Pass_Transitions_To_PendingAdminReview()
    {
        var asset = CreateTestAsset(AssetStatus.AutoValidating);
        asset.MarkPendingAdminReview();
        Assert.Equal(AssetStatus.PendingAdminReview, asset.Status);
    }

    [Fact]
    public void AutoValidation_Fail_Transitions_To_AutoFailed()
    {
        var asset = CreateTestAsset(AssetStatus.AutoValidating);
        asset.MarkAutoFailed();
        Assert.Equal(AssetStatus.AutoFailed, asset.Status);
    }

    [Fact]
    public void AutoFailed_Can_Reset_For_Regeneration()
    {
        var asset = CreateTestAsset(AssetStatus.AutoFailed);
        asset.ResetForRegeneration(2);
        Assert.Equal(AssetStatus.Queued, asset.Status);
        Assert.Equal(2, asset.Version);
    }

    [Fact]
    public void AdminSubmit_Transitions_To_PendingExpertReview()
    {
        var asset = CreateTestAsset(AssetStatus.PendingAdminReview);
        asset.MarkPendingExpertReview();
        Assert.Equal(AssetStatus.PendingExpertReview, asset.Status);
    }

    [Fact]
    public void ExpertApprove_Transitions_To_Approved()
    {
        var asset = CreateTestAsset(AssetStatus.PendingExpertReview);
        asset.MarkApproved();
        Assert.Equal(AssetStatus.Approved, asset.Status);
    }

    [Fact]
    public void ExpertReject_Transitions_To_Rejected()
    {
        var asset = CreateTestAsset(AssetStatus.PendingExpertReview);
        asset.MarkRejected();
        Assert.Equal(AssetStatus.Rejected, asset.Status);
    }

    [Fact]
    public void ExpertEditRequest_Transitions_To_EditRequested()
    {
        var asset = CreateTestAsset(AssetStatus.PendingExpertReview);
        asset.MarkEditRequested();
        Assert.Equal(AssetStatus.EditRequested, asset.Status);
    }

    [Fact]
    public void Rejected_Can_Reset_For_Regeneration()
    {
        var asset = CreateTestAsset(AssetStatus.Rejected);
        asset.ResetForRegeneration(2);
        Assert.Equal(AssetStatus.Queued, asset.Status);
    }

    [Fact]
    public void EditRequested_Can_Reset_For_Regeneration()
    {
        var asset = CreateTestAsset(AssetStatus.EditRequested);
        asset.ResetForRegeneration(2);
        Assert.Equal(AssetStatus.Queued, asset.Status);
    }

    [Fact]
    public void Approved_Can_Be_Invalidated()
    {
        var asset = CreateTestAsset(AssetStatus.Approved);
        asset.MarkInvalidated();
        Assert.Equal(AssetStatus.Invalidated, asset.Status);
    }

    [Fact]
    public void Approved_Can_Be_Superseded()
    {
        var asset = CreateTestAsset(AssetStatus.Approved);
        asset.MarkSuperseded();
        Assert.Equal(AssetStatus.Superseded, asset.Status);
    }

    // --- Forbidden transitions ---

    [Fact]
    public void Cannot_Skip_AutoValidation()
    {
        var asset = CreateTestAsset(AssetStatus.Queued);
        Assert.Throws<InvalidOperationException>(() => asset.MarkPendingAdminReview());
    }

    [Fact]
    public void Cannot_Skip_AdminReview()
    {
        var asset = CreateTestAsset(AssetStatus.AutoValidating);
        Assert.Throws<InvalidOperationException>(() => asset.MarkPendingExpertReview());
    }

    [Fact]
    public void Cannot_Approve_Without_ExpertReview()
    {
        var asset = CreateTestAsset(AssetStatus.PendingAdminReview);
        Assert.Throws<InvalidOperationException>(() => asset.MarkApproved());
    }

    [Fact]
    public void Rejected_Cannot_Transition_To_Approved_Directly()
    {
        var asset = CreateTestAsset(AssetStatus.Rejected);
        Assert.Throws<InvalidOperationException>(() => asset.MarkApproved());
    }

    [Fact]
    public void AutoFailed_Cannot_Go_Directly_To_Review()
    {
        var asset = CreateTestAsset(AssetStatus.AutoFailed);
        Assert.Throws<InvalidOperationException>(() => asset.MarkPendingAdminReview());
    }

    [Fact]
    public void Cannot_Regenerate_From_Approved()
    {
        var asset = CreateTestAsset(AssetStatus.Approved);
        Assert.Throws<InvalidOperationException>(() => asset.ResetForRegeneration(2));
    }

    [Fact]
    public void Cannot_Approve_From_Queued()
    {
        var asset = CreateTestAsset(AssetStatus.Queued);
        Assert.Throws<InvalidOperationException>(() => asset.MarkApproved());
    }

    // --- ReviewStateMachine tests ---

    [Theory]
    [InlineData(AssetStatus.Queued, AssetStatus.Producing, true)]
    [InlineData(AssetStatus.Producing, AssetStatus.AutoValidating, true)]
    [InlineData(AssetStatus.AutoValidating, AssetStatus.PendingAdminReview, true)]
    [InlineData(AssetStatus.AutoValidating, AssetStatus.AutoFailed, true)]
    [InlineData(AssetStatus.PendingAdminReview, AssetStatus.PendingExpertReview, true)]
    [InlineData(AssetStatus.PendingExpertReview, AssetStatus.Approved, true)]
    [InlineData(AssetStatus.PendingExpertReview, AssetStatus.Rejected, true)]
    [InlineData(AssetStatus.PendingExpertReview, AssetStatus.EditRequested, true)]
    [InlineData(AssetStatus.Queued, AssetStatus.Approved, false)]
    [InlineData(AssetStatus.AutoFailed, AssetStatus.PendingAdminReview, false)]
    [InlineData(AssetStatus.PendingAdminReview, AssetStatus.Approved, false)]
    [InlineData(AssetStatus.Rejected, AssetStatus.Approved, false)]
    public void StateMachine_Validates_Transitions(AssetStatus from, AssetStatus to, bool expected)
    {
        Assert.Equal(expected, ReviewStateMachine.IsTransitionAllowed(from, to));
    }

    [Fact]
    public void StateMachine_Invalidated_And_Superseded_Are_Terminal()
    {
        Assert.True(ReviewStateMachine.IsTerminal(AssetStatus.Invalidated));
        Assert.True(ReviewStateMachine.IsTerminal(AssetStatus.Superseded));
        Assert.False(ReviewStateMachine.IsTerminal(AssetStatus.Approved));
    }

    // --- ReviewDecision validation ---

    [Fact]
    public void Expert_Reject_Requires_FixInstruction()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReviewDecision.CreateExpertDecision(
                Guid.NewGuid(), ReviewOutcome.Rejected, "expert-1",
                null, null, null));
    }

    [Fact]
    public void Expert_EditRequest_Requires_Scope()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReviewDecision.CreateExpertDecision(
                Guid.NewGuid(), ReviewOutcome.EditRequested, "expert-1",
                "fix the narration", null, null));
    }

    [Fact]
    public void Expert_EditRequest_With_Scope_Succeeds()
    {
        var decision = ReviewDecision.CreateExpertDecision(
            Guid.NewGuid(), ReviewOutcome.EditRequested, "expert-1",
            "fix the narration", "narration", null);

        Assert.Equal(ReviewOutcome.EditRequested, decision.Outcome);
        Assert.Equal("narration", decision.Scope);
    }

    // --- ReviewAssignment validation ---

    [Fact]
    public void Expert_Assignment_Subject_Mismatch_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReviewAssignment.CreateExpertAssignment(
                Guid.NewGuid(), "expert-1", "admin-1",
                Subject.Mathematics, Subject.Science, AssetType.TextSummary));
    }

    [Fact]
    public void Expert_Assignment_Subject_Match_Succeeds()
    {
        var assignment = ReviewAssignment.CreateExpertAssignment(
            Guid.NewGuid(), "expert-1", "admin-1",
            Subject.Mathematics, Subject.Mathematics, AssetType.TextSummary);

        Assert.Equal(ReviewAssignmentStatus.Open, assignment.Status);
        Assert.Equal(ReviewTier.ExpertReview, assignment.Tier);
    }

    // --- PublishedAsset validation ---

    [Fact]
    public void PublishedAsset_Create_Sets_Active_Status()
    {
        var published = Muallimi.Domain.Publication.PublishedAsset.Create(
            Guid.NewGuid(), Guid.NewGuid(), AssetType.TextSummary, null,
            "/content/test", "admin-1", "expert-1", 1);

        Assert.True(published.IsActive);
        Assert.Equal(PublishedAssetStatus.Active, published.Status);
    }

    [Fact]
    public void PublishedAsset_Cannot_Invalidate_NonActive()
    {
        var published = Muallimi.Domain.Publication.PublishedAsset.Create(
            Guid.NewGuid(), Guid.NewGuid(), AssetType.TextSummary, null,
            "/content/test", "admin-1", "expert-1", 1);

        published.Invalidate();
        Assert.Throws<InvalidOperationException>(() => published.Invalidate());
    }
}
