using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Review;

/// <summary>
/// An approval, rejection, or edit request recorded against an asset.
/// Fix instruction is required for rejected and edit_requested outcomes.
/// Every decision forms part of the audit trail for publication.
/// </summary>
public class ReviewDecision
{
    public Guid DecisionId { get; private set; }
    public Guid AssetId { get; private set; }
    public ReviewTier Tier { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public ReviewOutcome Outcome { get; private set; }
    public string? Scope { get; private set; }
    public string? FixInstruction { get; private set; }
    public DateTime DecidedAt { get; private set; }
    public string? CorrelationId { get; private set; }

    private ReviewDecision() { }

    /// <summary>
    /// Record an auto-validation decision (Tier 1 — system actor).
    /// </summary>
    public static ReviewDecision CreateAutoValidation(
        Guid assetId, ReviewOutcome outcome, string? correlationId)
    {
        return new ReviewDecision
        {
            DecisionId = Guid.NewGuid(),
            AssetId = assetId,
            Tier = ReviewTier.AutoValidation,
            ActorId = "system",
            Outcome = outcome,
            DecidedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        };
    }

    /// <summary>
    /// Record a Curriculum Admin review decision (Tier 2).
    /// Admin can approve (submit to expert) or request regeneration.
    /// </summary>
    public static ReviewDecision CreateAdminDecision(
        Guid assetId, ReviewOutcome outcome, string actorId, string? correlationId)
    {
        return new ReviewDecision
        {
            DecisionId = Guid.NewGuid(),
            AssetId = assetId,
            Tier = ReviewTier.AdminReview,
            ActorId = actorId,
            Outcome = outcome,
            DecidedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        };
    }

    /// <summary>
    /// Record a Subject Expert review decision (Tier 3).
    /// Reject and edit-request require a fix instruction.
    /// Edit-request also requires a scope (named pipeline stage).
    /// </summary>
    public static ReviewDecision CreateExpertDecision(
        Guid assetId, ReviewOutcome outcome, string actorId,
        string? fixInstruction, string? scope, string? correlationId)
    {
        if (outcome is ReviewOutcome.Rejected or ReviewOutcome.EditRequested
            && string.IsNullOrWhiteSpace(fixInstruction))
        {
            throw new InvalidOperationException(
                $"A fix instruction is required when outcome is {outcome}.");
        }

        if (outcome == ReviewOutcome.EditRequested && string.IsNullOrWhiteSpace(scope))
        {
            throw new InvalidOperationException(
                "A pipeline stage scope is required for edit requests.");
        }

        return new ReviewDecision
        {
            DecisionId = Guid.NewGuid(),
            AssetId = assetId,
            Tier = ReviewTier.ExpertReview,
            ActorId = actorId,
            Outcome = outcome,
            FixInstruction = fixInstruction,
            Scope = scope,
            DecidedAt = DateTime.UtcNow,
            CorrelationId = correlationId
        };
    }
}
