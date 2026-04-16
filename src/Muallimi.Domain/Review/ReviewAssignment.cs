using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Review;

/// <summary>
/// Allocation of an asset to a reviewer queue (Tier 2 admin or Tier 3 expert).
/// Expert assignments must match the asset's subject expertise.
/// </summary>
public class ReviewAssignment
{
    public Guid AssignmentId { get; private set; }
    public Guid AssetId { get; private set; }
    public ReviewTier Tier { get; private set; }
    public string AssignedTo { get; private set; } = string.Empty;
    public string AssignedBy { get; private set; } = string.Empty;
    public DateTime AssignedAt { get; private set; }
    public DateTime SlaDueAt { get; private set; }
    public ReviewAssignmentStatus Status { get; private set; }

    private ReviewAssignment() { }

    /// <summary>
    /// Create an admin review assignment (Tier 2). SLA: 5 business days for text/audio, 7 for visuals.
    /// </summary>
    public static ReviewAssignment CreateAdminAssignment(
        Guid assetId, string assignedTo, string assignedBy, AssetType assetType)
    {
        var slaDays = assetType is AssetType.Visual ? 7 : 5;

        return new ReviewAssignment
        {
            AssignmentId = Guid.NewGuid(),
            AssetId = assetId,
            Tier = ReviewTier.AdminReview,
            AssignedTo = assignedTo,
            AssignedBy = assignedBy,
            AssignedAt = DateTime.UtcNow,
            SlaDueAt = ComputeBusinessDayDeadline(slaDays),
            Status = ReviewAssignmentStatus.Open
        };
    }

    /// <summary>
    /// Create an expert review assignment (Tier 3) with subject-match validation.
    /// </summary>
    public static ReviewAssignment CreateExpertAssignment(
        Guid assetId, string expertId, string assignedBy,
        Subject assetSubject, Subject expertSubject, AssetType assetType)
    {
        if (assetSubject != expertSubject)
            throw new InvalidOperationException(
                $"Expert subject mismatch: asset requires {assetSubject} but expert covers {expertSubject}.");

        var slaDays = assetType is AssetType.Visual ? 7 : 5;

        return new ReviewAssignment
        {
            AssignmentId = Guid.NewGuid(),
            AssetId = assetId,
            Tier = ReviewTier.ExpertReview,
            AssignedTo = expertId,
            AssignedBy = assignedBy,
            AssignedAt = DateTime.UtcNow,
            SlaDueAt = ComputeBusinessDayDeadline(slaDays),
            Status = ReviewAssignmentStatus.Open
        };
    }

    public void MarkInReview()
    {
        if (Status != ReviewAssignmentStatus.Open)
            throw new InvalidOperationException($"Cannot mark in-review from {Status}.");
        Status = ReviewAssignmentStatus.InReview;
    }

    public void MarkSubmitted()
    {
        if (Status != ReviewAssignmentStatus.Open && Status != ReviewAssignmentStatus.InReview)
            throw new InvalidOperationException($"Cannot mark submitted from {Status}.");
        Status = ReviewAssignmentStatus.Submitted;
    }

    public void MarkEscalated()
    {
        if (Status == ReviewAssignmentStatus.Submitted || Status == ReviewAssignmentStatus.Expired)
            throw new InvalidOperationException($"Cannot escalate from {Status}.");
        Status = ReviewAssignmentStatus.Escalated;
    }

    public void MarkExpired()
    {
        if (Status == ReviewAssignmentStatus.Submitted)
            throw new InvalidOperationException("Cannot expire a submitted assignment.");
        Status = ReviewAssignmentStatus.Expired;
    }

    public bool IsOverdue => DateTime.UtcNow > SlaDueAt && Status != ReviewAssignmentStatus.Submitted;

    private static DateTime ComputeBusinessDayDeadline(int businessDays)
    {
        var deadline = DateTime.UtcNow;
        var added = 0;
        while (added < businessDays)
        {
            deadline = deadline.AddDays(1);
            if (deadline.DayOfWeek != DayOfWeek.Friday && deadline.DayOfWeek != DayOfWeek.Saturday)
                added++;
        }
        return deadline;
    }
}
