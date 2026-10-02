namespace Muallimi.Domain.Curriculum;

/// <summary>
/// One row per node the reviewer has flipped to a different retrieval class
/// than the classifier assigned. Absence of a row means "use the node's
/// <c>system_retrieval_class</c> from the tree JSONB unchanged."
///
/// Design (Stage 6): overrides live in a SIDE table rather than being
/// written back into the tree JSONB so that
///   (a) the tree stays clean for analytics (immutable classifier verdict), and
///   (b) we avoid the awkward jsonb_set on every reviewer edit.
///
/// A reset-to-auto action is modeled as deleting the row, not writing
/// <c>NULL</c> — makes "which nodes have overrides?" a trivial count-star
/// query for the prompt-iteration dashboard.
/// </summary>
public class CurriculumNodeRetrievalOverride
{
    public Guid NodeId { get; private set; }
    public Guid SourceId { get; private set; }
    public RetrievalClass RetrievalClass { get; private set; }
    public string OverriddenByUserId { get; private set; } = string.Empty;
    public DateTime OverriddenAt { get; private set; }

    private CurriculumNodeRetrievalOverride() { } // EF Core

    public static CurriculumNodeRetrievalOverride Create(
        Guid nodeId,
        Guid sourceId,
        RetrievalClass retrievalClass,
        string overriddenByUserId)
    {
        if (nodeId == Guid.Empty)
            throw new ArgumentException("node_id required.", nameof(nodeId));
        if (sourceId == Guid.Empty)
            throw new ArgumentException("source_id required.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(overriddenByUserId))
            throw new ArgumentException("overridden_by required.", nameof(overriddenByUserId));

        return new CurriculumNodeRetrievalOverride
        {
            NodeId = nodeId,
            SourceId = sourceId,
            RetrievalClass = retrievalClass,
            OverriddenByUserId = overriddenByUserId,
            OverriddenAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Change the effective class on an existing override row. Bumps the
    /// timestamp + attribution so audit trails reflect the latest reviewer.
    /// </summary>
    public void UpdateClass(RetrievalClass retrievalClass, string overriddenByUserId)
    {
        if (string.IsNullOrWhiteSpace(overriddenByUserId))
            throw new ArgumentException("overridden_by required.", nameof(overriddenByUserId));

        RetrievalClass = retrievalClass;
        OverriddenByUserId = overriddenByUserId;
        OverriddenAt = DateTime.UtcNow;
    }
}
