using Muallimi.Domain.Shared;

namespace Muallimi.Api.RetrievalApi;

/// <summary>
/// T099 - Cross-scope leakage guard for runtime retrieval.
/// Validates that all returned data items belong to the requested scope.
/// A scope violation is a contract failure per the runtime retrieval contract.
/// </summary>
public static class ScopeIsolationFilter
{
    /// <summary>
    /// Validates that every chunk result belongs to the requested curriculum scope.
    /// Throws if any chunk leaks from a different scope.
    /// </summary>
    public static void AssertChunksInScope(
        List<ChunkResult> chunks,
        CurriculumType requestedType,
        Grade requestedGrade,
        Subject requestedSubject)
    {
        foreach (var chunk in chunks)
        {
            // Parse the metadata JSON to validate scope
            // The metadata field contains curriculum_type, grade, subject
            var metadata = System.Text.Json.JsonDocument.Parse(chunk.Metadata);
            var root = metadata.RootElement;

            if (root.TryGetProperty("curriculum_type", out var ctProp))
            {
                var ct = ctProp.GetString();
                if (!string.IsNullOrEmpty(ct) && !ct.Equals(requestedType.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new ScopeLeakageException(
                        $"Chunk {chunk.ChunkId} has curriculum_type '{ct}' but request scope is '{requestedType}'.");
                }
            }

            if (root.TryGetProperty("grade", out var gradeProp))
            {
                var gr = gradeProp.GetString();
                if (!string.IsNullOrEmpty(gr) && !gr.Equals(requestedGrade.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new ScopeLeakageException(
                        $"Chunk {chunk.ChunkId} has grade '{gr}' but request scope is '{requestedGrade}'.");
                }
            }

            if (root.TryGetProperty("subject", out var subjectProp))
            {
                var sub = subjectProp.GetString();
                if (!string.IsNullOrEmpty(sub) && !sub.Equals(requestedSubject.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new ScopeLeakageException(
                        $"Chunk {chunk.ChunkId} has subject '{sub}' but request scope is '{requestedSubject}'.");
                }
            }
        }
    }

    /// <summary>
    /// Validates that visual/audio assets returned belong to the expected lesson scope.
    /// </summary>
    public static void AssertAssetsInScope(
        IEnumerable<(Guid assetLessonId, Guid requestedLessonId)> assetLessonPairs)
    {
        foreach (var (assetLessonId, requestedLessonId) in assetLessonPairs)
        {
            if (assetLessonId != requestedLessonId)
            {
                throw new ScopeLeakageException(
                    $"Asset belongs to lesson '{assetLessonId}' but was requested for lesson '{requestedLessonId}'.");
            }
        }
    }
}

public class ScopeLeakageException : Exception
{
    public ScopeLeakageException(string message) : base(message) { }
}
