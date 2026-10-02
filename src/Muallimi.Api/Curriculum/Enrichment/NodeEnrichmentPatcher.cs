using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Generic;

namespace Muallimi.Api.Curriculum.Enrichment;

public sealed record NodeLookup(string Title, string NodeType, JsonObject Node);

public static class NodeEnrichmentPatcher
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static NodeLookup? FindNode(string rawJson, Guid nodeId)
    {
        var root = JsonNode.Parse(rawJson);
        var nodes = ExtractTopArray(root);
        if (nodes is null) return null;

        foreach (var n in nodes)
        {
            var hit = FindRecursive(n, nodeId);
            if (hit is not null) return hit;
        }
        return null;
    }

    public static string PatchEnrichment(string rawJson, Guid nodeId, string summary, IEnumerable<string> keywords)
    {
        var root = JsonNode.Parse(rawJson) ?? throw new InvalidOperationException("Empty tree JSON.");
        var nodes = ExtractTopArray(root) ?? throw new InvalidOperationException("Tree has no nodes array.");

        JsonObject? target = null;
        foreach (var n in nodes)
        {
            target = FindObjectRecursive(n, nodeId);
            if (target is not null) break;
        }

        if (target is null)
            throw new InvalidOperationException($"Node {nodeId} not found in tree.");

        target["summary"] = summary;
        var kwArr = new JsonArray();
        foreach (var kw in keywords) kwArr.Add(kw);
        target["concept_keywords"] = kwArr;

        return root.ToJsonString(WriteOptions);
    }

    private static JsonArray? ExtractTopArray(JsonNode? root) => root switch
    {
        JsonArray arr => arr,
        JsonObject obj when obj["nodes"] is JsonArray nested => nested,
        _ => null,
    };

    private static NodeLookup? FindRecursive(JsonNode? node, Guid nodeId)
    {
        var obj = FindObjectRecursive(node, nodeId);
        if (obj is null) return null;
        var title = obj["title"]?.GetValue<string>() ?? string.Empty;
        var nodeType = obj["node_type"]?.GetValue<string>() ?? string.Empty;
        return new NodeLookup(title, nodeType, obj);
    }

    private static JsonObject? FindObjectRecursive(JsonNode? node, Guid nodeId)
    {
        if (node is not JsonObject obj) return null;

        var idStr = obj["id"]?.GetValue<string>() ?? obj["node_id"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(idStr) && Guid.TryParse(idStr, out var parsed) && parsed == nodeId)
            return obj;

        if (obj["children"] is JsonArray children)
        {
            foreach (var c in children)
            {
                var hit = FindObjectRecursive(c, nodeId);
                if (hit is not null) return hit;
            }
        }
        return null;
    }
}
