using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Curriculum.Enrichment;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;
using Pgvector;

namespace Muallimi.Api.Curriculum.Embeddings;

public sealed record NodeEmbeddingResult(
    Guid NodeId,
    string ProviderKey,
    string ModelName,
    int Dim,
    string EmbedBodySha256,
    bool Skipped,
    string? SkipReason);

public interface INodeEmbeddingService
{
    Task<NodeEmbeddingResult> EmbedNodeAsync(Guid nodeId, CancellationToken ct = default);
}

public sealed class NodeEmbeddingService : INodeEmbeddingService
{
    private readonly MuallimiDbContext _db;
    private readonly IEmbeddingProvider _provider;
    private readonly ILogger<NodeEmbeddingService> _logger;

    public NodeEmbeddingService(
        MuallimiDbContext db,
        IEmbeddingProvider provider,
        ILogger<NodeEmbeddingService> logger)
    {
        _db = db;
        _provider = provider;
        _logger = logger;
    }

    public async Task<NodeEmbeddingResult> EmbedNodeAsync(Guid nodeId, CancellationToken ct = default)
    {
        var content = await _db.CurriculumNodeContents.FindAsync(new object?[] { nodeId }, ct)
            ?? throw new InvalidOperationException($"No content row for node {nodeId}.");
        var source = await _db.CurriculumSources.FindAsync(new object?[] { content.SourceId }, ct)
            ?? throw new InvalidOperationException($"No source {content.SourceId} for node {nodeId}.");
        var structure = await _db.CurriculumStructures.FirstOrDefaultAsync(s => s.SourceId == source.SourceId, ct)
            ?? throw new InvalidOperationException($"No structure tree for source {source.SourceId}.");

        var lookup = LocateNode(structure.Nodes, nodeId)
            ?? throw new InvalidOperationException($"Node {nodeId} not in tree {structure.SourceId}.");

        var body = ComposeEmbedBody(lookup.Title, lookup.Summary, lookup.ConceptKeywords, content.Markdown ?? string.Empty);
        var sha = Sha256Hex(body);

        var existing = await _db.CurriculumNodeEmbeddings.FindAsync(new object?[] { nodeId }, ct);

        if (_provider.ProviderKey == "null")
        {
            _logger.LogInformation("Embedding skipped for node {NodeId}: provider=null.", nodeId);
            return new NodeEmbeddingResult(nodeId, "null", "null", 0, sha, Skipped: true, SkipReason: "provider_null");
        }

        var result = await _provider.EmbedAsync(body, ct)
            ?? throw new InvalidOperationException($"Provider {_provider.ProviderKey} returned null embedding.");

        var subject = source.Subject.ToString();
        var language = source.TutorLanguage.ToString();
        var retrievalClass = lookup.RetrievalClass;

        var vec = new Vector(result.Vector);

        if (existing is null)
        {
            existing = new CurriculumNodeEmbedding
            {
                NodeId = nodeId,
                SourceId = source.SourceId,
                Subject = subject,
                Language = language,
                RetrievalClass = retrievalClass,
                EmbedBodySha256 = sha,
                ProviderKey = result.ProviderKey,
                ModelName = result.ModelName,
                Dim = result.Dim,
                EmbeddedAt = DateTime.UtcNow,
            };
            AssignVector(existing, result.ProviderKey, vec);
            _db.CurriculumNodeEmbeddings.Add(existing);
        }
        else
        {
            existing.Subject = subject;
            existing.Language = language;
            existing.RetrievalClass = retrievalClass;
            existing.EmbedBodySha256 = sha;
            existing.ProviderKey = result.ProviderKey;
            existing.ModelName = result.ModelName;
            existing.Dim = result.Dim;
            existing.EmbeddedAt = DateTime.UtcNow;
            AssignVector(existing, result.ProviderKey, vec);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Embedded node {NodeId} via {Provider}/{Model} dim={Dim} sha256={Sha}...",
            nodeId, result.ProviderKey, result.ModelName, result.Dim, sha[..12]);

        return new NodeEmbeddingResult(nodeId, result.ProviderKey, result.ModelName, result.Dim, sha, Skipped: false, SkipReason: null);
    }

    private static void AssignVector(CurriculumNodeEmbedding row, string providerKey, Vector vec)
    {
        switch (providerKey)
        {
            case "voyage": row.VoyageEmbedding = vec; break;
            case "openai": row.OpenAiEmbedding = vec; break;
            case "local": row.LocalEmbedding = vec; break;
            default: throw new InvalidOperationException($"No embedding column mapped for provider '{providerKey}'.");
        }
    }

    private static string ComposeEmbedBody(string title, string summary, IReadOnlyList<string> keywords, string body)
    {
        var sb = new StringBuilder();
        sb.Append(title.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(summary))
            sb.Append("Summary: ").Append(summary.Trim()).Append("\n\n");
        if (keywords is { Count: > 0 })
            sb.Append("Keywords: ").Append(string.Join(", ", keywords)).Append("\n\n");
        sb.Append(body.Trim());
        return sb.ToString();
    }

    private static string Sha256Hex(string body)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private sealed record LookupResult(string Title, string RetrievalClass, string Summary, IReadOnlyList<string> ConceptKeywords);

    private static LookupResult? LocateNode(string rawJson, Guid nodeId)
    {
        var root = JsonNode.Parse(rawJson);
        JsonArray? nodes = root switch
        {
            JsonArray arr => arr,
            JsonObject obj when obj["nodes"] is JsonArray n => n,
            _ => null,
        };
        if (nodes is null) return null;

        foreach (var n in nodes)
        {
            var hit = Recurse(n, nodeId);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static LookupResult? Recurse(JsonNode? node, Guid nodeId)
    {
        if (node is not JsonObject obj) return null;

        var idStr = obj["id"]?.GetValue<string>() ?? obj["node_id"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(idStr) && Guid.TryParse(idStr, out var parsed) && parsed == nodeId)
        {
            var kws = new List<string>();
            if (obj["concept_keywords"] is JsonArray kwArr)
                foreach (var kw in kwArr)
                    if (kw is not null) kws.Add(kw.GetValue<string>());

            return new LookupResult(
                Title: obj["title"]?.GetValue<string>() ?? string.Empty,
                RetrievalClass: obj["system_retrieval_class"]?.GetValue<string>() ?? "structural",
                Summary: obj["summary"]?.GetValue<string>() ?? string.Empty,
                ConceptKeywords: kws);
        }

        if (obj["children"] is JsonArray children)
        {
            foreach (var c in children)
            {
                var hit = Recurse(c, nodeId);
                if (hit is not null) return hit;
            }
        }
        return null;
    }
}
