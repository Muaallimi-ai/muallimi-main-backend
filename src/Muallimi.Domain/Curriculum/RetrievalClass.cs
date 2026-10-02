namespace Muallimi.Domain.Curriculum;

/// <summary>
/// Retrieval-index membership for a curriculum node — decides both
/// (a) whether the node is embedded into the vector index in Stage 7, and
/// (b) whether the tutor may ground free-chat answers on it.
///
/// Locked-in per project memory <c>project_retrieval_strategy.md</c>
/// (decisions D1/D2/D6 in mvp-execution-plan.md):
///   - Teachable  : full text is embedded and free-chat retrieval may hit it.
///   - Assessment : indexed for lesson-scoped quiz-generator queries only —
///                  NEVER surfaced by free-chat retrieval so answer keys
///                  can't leak.
///   - Procedural : instructions / discussion prompts / hands-on activities
///                  that a tutor should not paraphrase or teach from.
///   - Structural : container-only nodes (chapters, lessons, ToCs) plus
///                  strictly-decorative sidebars — nothing to retrieve.
///
/// Serialized as a lowercase string ("teachable" / "assessment" /
/// "procedural" / "structural") both in the JSONB tree and on the wire.
/// Do NOT reorder these enum values — persistence uses names, not ordinals,
/// but reordering can still confuse code that assumes any ordering.
/// </summary>
public enum RetrievalClass
{
    Teachable,
    Assessment,
    Procedural,
    Structural,
}

/// <summary>
/// Auto-classifies a node's <c>node_type</c> into a <see cref="RetrievalClass"/>
/// using the mapping locked in D6 (see mvp-execution-plan.md §3) plus the
/// Egyptian-Prep-math extensions added when Stage 5's math/ar/skeleton.v2
/// prompt introduced <c>worked_example</c>, <c>self_assessment</c>,
/// <c>warm_up</c>, <c>discussion_prompt</c>, <c>note_sidebar</c>,
/// <c>general_rule</c>, <c>vocabulary_sidebar</c>, etc.
///
/// Called at Phase A completion to stamp <c>system_retrieval_class</c> on
/// every node in the tree (immutable — analytics). The reviewer's overridden
/// <c>retrieval_class</c> lives separately in
/// <c>curriculum_node_retrieval_overrides</c>; the classifier only writes
/// the system value, never the effective one.
/// </summary>
public static class RetrievalClassClassifier
{
    /// <summary>
    /// Case-insensitive lookup. Unknown or missing <paramref name="nodeType"/>
    /// falls back to <see cref="RetrievalClass.Structural"/> — the safest
    /// default because it excludes the node from retrieval (no risk of
    /// leaking assessment answers or teaching from junk).
    /// </summary>
    public static RetrievalClass Classify(string? nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
            return RetrievalClass.Structural;

        return nodeType.Trim().ToLowerInvariant() switch
        {
            // Teachable — the AI tutor may ground free-chat answers on these.
            "topic" => RetrievalClass.Teachable,
            "subtopic" => RetrievalClass.Teachable,
            "concept" => RetrievalClass.Teachable,
            "explanation" => RetrievalClass.Teachable,
            "definition" => RetrievalClass.Teachable,
            "worked_example" => RetrievalClass.Teachable,
            "note_sidebar" => RetrievalClass.Teachable,
            "general_rule" => RetrievalClass.Teachable,
            "warm_up" => RetrievalClass.Teachable,
            "vocabulary_sidebar" => RetrievalClass.Teachable,

            // Assessment — reachable via lesson-scoped queries (quiz generator)
            // but NEVER surfaced by free-chat retrieval.
            "self_assessment" => RetrievalClass.Assessment,
            "assessment_section" => RetrievalClass.Assessment,
            "creative_thinking" => RetrievalClass.Assessment,
            "question" => RetrievalClass.Assessment,
            "quiz" => RetrievalClass.Assessment,
            "drill" => RetrievalClass.Assessment,
            "assessment" => RetrievalClass.Assessment,
            "problem" => RetrievalClass.Assessment,
            "exercise" => RetrievalClass.Assessment,

            // Procedural — instructions / activities the tutor should not
            // teach from.
            "discussion_prompt" => RetrievalClass.Procedural,
            "critical_thinking" => RetrievalClass.Procedural,
            "cooperative_activity" => RetrievalClass.Procedural,
            "unit_project" => RetrievalClass.Procedural,
            "activity" => RetrievalClass.Procedural,
            "lab" => RetrievalClass.Procedural,
            "project" => RetrievalClass.Procedural,
            "investigation" => RetrievalClass.Procedural,

            // Structural — containers + decorative sidebars.
            "chapter" => RetrievalClass.Structural,
            "lesson" => RetrievalClass.Structural,
            "section" => RetrievalClass.Structural,
            "section_header" => RetrievalClass.Structural,
            "chapter_intro" => RetrievalClass.Structural,
            "toc" => RetrievalClass.Structural,
            "glossary" => RetrievalClass.Structural,
            "learning_outcomes" => RetrievalClass.Structural,
            "technology_tip" => RetrievalClass.Structural,

            _ => RetrievalClass.Structural,
        };
    }

    /// <summary>
    /// Canonical lowercase string form ("teachable" / "assessment" /
    /// "procedural" / "structural"). This is the value stamped on the
    /// tree JSONB and returned by API endpoints — kept as a helper so
    /// callers don't accidentally emit PascalCase.
    /// </summary>
    public static string ToWireString(this RetrievalClass rc) => rc.ToString().ToLowerInvariant();

    /// <summary>
    /// Parses the lowercase wire string back to the enum. Returns
    /// <see cref="RetrievalClass.Structural"/> for anything unrecognized —
    /// same safe-default policy as <see cref="Classify"/>.
    /// </summary>
    public static RetrievalClass Parse(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
            return RetrievalClass.Structural;

        return wire.Trim().ToLowerInvariant() switch
        {
            "teachable" => RetrievalClass.Teachable,
            "assessment" => RetrievalClass.Assessment,
            "procedural" => RetrievalClass.Procedural,
            "structural" => RetrievalClass.Structural,
            _ => RetrievalClass.Structural,
        };
    }
}
