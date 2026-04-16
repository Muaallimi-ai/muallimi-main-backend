using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Review;

/// <summary>
/// Centralized state-machine enforcement for GeneratedAsset review transitions.
/// Covers every allowed and forbidden transition per review-state-contract.md.
///
/// States: queued -> producing -> auto_validating -> auto_failed | pending_admin_review
///         -> pending_expert_review -> approved | rejected | edit_requested
///         approved -> invalidated | superseded
///
/// Critical invariants:
/// - approved is ONLY reachable from pending_expert_review
/// - No direct path from auto_failed to review queue
/// - Every transition records actor, timestamp, correlation ID
/// </summary>
public static class ReviewStateMachine
{
    private static readonly Dictionary<AssetStatus, HashSet<AssetStatus>> AllowedTransitions = new()
    {
        [AssetStatus.Queued] = new() { AssetStatus.Producing },
        [AssetStatus.Producing] = new() { AssetStatus.AutoValidating },
        [AssetStatus.AutoValidating] = new() { AssetStatus.AutoFailed, AssetStatus.PendingAdminReview },
        [AssetStatus.AutoFailed] = new() { AssetStatus.Queued },
        [AssetStatus.PendingAdminReview] = new() { AssetStatus.PendingExpertReview, AssetStatus.Queued },
        [AssetStatus.PendingExpertReview] = new() { AssetStatus.Approved, AssetStatus.Rejected, AssetStatus.EditRequested },
        [AssetStatus.EditRequested] = new() { AssetStatus.Producing },
        [AssetStatus.Rejected] = new() { AssetStatus.Queued },
        [AssetStatus.Approved] = new() { AssetStatus.Invalidated, AssetStatus.Superseded },
        [AssetStatus.Invalidated] = new(),
        [AssetStatus.Superseded] = new()
    };

    /// <summary>
    /// Returns true if the transition from currentStatus to targetStatus is allowed.
    /// </summary>
    public static bool IsTransitionAllowed(AssetStatus currentStatus, AssetStatus targetStatus)
    {
        return AllowedTransitions.TryGetValue(currentStatus, out var allowed)
               && allowed.Contains(targetStatus);
    }

    /// <summary>
    /// Validates a transition and throws if forbidden.
    /// </summary>
    public static void ValidateTransition(AssetStatus currentStatus, AssetStatus targetStatus)
    {
        if (!IsTransitionAllowed(currentStatus, targetStatus))
            throw new InvalidOperationException(
                $"Forbidden state transition: {currentStatus} -> {targetStatus}. " +
                $"Allowed targets from {currentStatus}: [{string.Join(", ", GetAllowedTargets(currentStatus))}].");
    }

    /// <summary>
    /// Returns the set of states reachable from the given status.
    /// </summary>
    public static IReadOnlySet<AssetStatus> GetAllowedTargets(AssetStatus currentStatus)
    {
        return AllowedTransitions.TryGetValue(currentStatus, out var allowed)
            ? allowed
            : new HashSet<AssetStatus>();
    }

    /// <summary>
    /// Returns true if the status is a terminal state (no further transitions possible).
    /// </summary>
    public static bool IsTerminal(AssetStatus status)
    {
        return status is AssetStatus.Invalidated or AssetStatus.Superseded;
    }

    /// <summary>
    /// Returns true if the asset is in a state that can enter human review.
    /// </summary>
    public static bool IsInHumanReview(AssetStatus status)
    {
        return status is AssetStatus.PendingAdminReview or AssetStatus.PendingExpertReview;
    }

    /// <summary>
    /// Returns true if the asset is approved and eligible for runtime retrieval.
    /// </summary>
    public static bool IsPublishable(AssetStatus status)
    {
        return status == AssetStatus.Approved;
    }
}
