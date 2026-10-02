using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muallimi.Domain.Curriculum;

/// <summary>
/// Walks a curriculum structure tree (as it arrives from the ingestion
/// worker in raw JSON string form) and stamps <c>system_retrieval_class</c>
/// onto every node, computed from that node's <c>node_type</c> via
/// <see cref="RetrievalClassClassifier.Classify"/>.
///
/// This runs once, at Phase A completion, before the tree is persisted to
/// <c>curriculum_structures.nodes</c>. The stamped value is treated as
/// immutable — reviewer overrides live in a separate table so this JSONB
/// snapshot stays clean for analytics ("what did the classifier say
/// originally?"). See Stage 6 in
/// specs/003-curriculum-content-ingestion/phased-implementation-plan.md.
///
/// Idempotent: re-enriching a tree that already has the field just re-runs
/// the classifier and overwrites the value. Backfill for existing sources
/// (extracted before Stage 6 shipped) calls this on their persisted trees.
/// </summary>
public static class RetrievalClassTreeEnricher
{
    /// <summary>
    /// Parses <paramref name="rawJson"/> as a JSON array of tree nodes,
    /// stamps <c>system_retrieval_class</c> on each node (and every
    /// descendant), and returns the re-serialized JSON string ready to
    /// store in <c>curriculum_structures.nodes</c>.
    /// </summary>
    /// <remarks>
    /// Accepts either a top-level array (the shape the worker emits after
    /// mapping) or a top-level object with a <c>nodes</c> array — both are
    /// tolerated so this can enrich fresh worker payloads and legacy DB
    /// payloads uniformly.
    /// </remarks>
    public static string EnrichJson(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return rawJson;

        var root = JsonNode.Parse(rawJson);
        if (root is null) return rawJson;

        JsonArray? nodes = root switch
        {
            JsonArray arr => arr,
            JsonObject obj when obj["nodes"] is JsonArray nested => nested,
            _ => null,
        };
        if (nodes is null) return rawJson;

        foreach (var node in nodes)
        {
            EnrichNode(node);
        }

        return root.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static void EnrichNode(JsonNode? node)
    {
        if (node is not JsonObject obj) return;

        var nodeType = obj["node_type"]?.GetValue<string>();
        var systemClass = RetrievalClassClassifier.Classify(nodeType);
        obj["system_retrieval_class"] = systemClass.ToWireString();

        if (obj["children"] is JsonArray children)
        {
            foreach (var child in children)
            {
                EnrichNode(child);
            }
        }
    }
}
