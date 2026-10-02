using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Curriculum.Prompts;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Curriculum.Enrichment;

public sealed record EnrichmentResult(
    string Summary,
    IReadOnlyList<string> ConceptKeywords,
    string PromptKey,
    string PromptVersion,
    string PromptSha256,
    string ModelName);

public interface INodeEnrichmentService
{
    Task<EnrichmentResult?> EnrichNodeAsync(Guid nodeId, CancellationToken ct = default);
}

public sealed class NodeEnrichmentService : INodeEnrichmentService
{
    private readonly MuallimiDbContext _db;
    private readonly IPromptRegistry _prompts;
    private readonly ClaudeTextClient _claude;
    private readonly ILogger<NodeEnrichmentService> _logger;

    public NodeEnrichmentService(
        MuallimiDbContext db,
        IPromptRegistry prompts,
        ClaudeTextClient claude,
        ILogger<NodeEnrichmentService> logger)
    {
        _db = db;
        _prompts = prompts;
        _claude = claude;
        _logger = logger;
    }

    public async Task<EnrichmentResult?> EnrichNodeAsync(Guid nodeId, CancellationToken ct = default)
    {
        var content = await _db.CurriculumNodeContents.FindAsync(new object?[] { nodeId }, ct);
        if (content is null)
        {
            _logger.LogWarning("Enrichment skipped: no content row for node {NodeId}.", nodeId);
            return null;
        }
        if (string.IsNullOrWhiteSpace(content.Markdown))
        {
            _logger.LogWarning("Enrichment skipped: node {NodeId} has no markdown body.", nodeId);
            return null;
        }

        var source = await _db.CurriculumSources.FindAsync(new object?[] { content.SourceId }, ct)
            ?? throw new InvalidOperationException($"Source {content.SourceId} not found for node {nodeId}.");

        var structure = await _db.CurriculumStructures.FirstOrDefaultAsync(s => s.SourceId == source.SourceId, ct)
            ?? throw new InvalidOperationException($"No structure tree persisted for source {source.SourceId}.");

        var lookup = NodeEnrichmentPatcher.FindNode(structure.Nodes, nodeId)
            ?? throw new InvalidOperationException($"Node {nodeId} not found in tree for source {source.SourceId}.");

        var subject = source.Subject.ToString();
        var language = source.TutorLanguage.ToString();
        var promptRes = _prompts.Resolve(subject, language, "enrichment");

        var body = promptRes.Body
            .Replace("{{title}}", lookup.Title)
            .Replace("{{node_type}}", lookup.NodeType)
            .Replace("{{body}}", content.Markdown);

        _logger.LogInformation(
            "Enriching node {NodeId} (subject={Subject}, lang={Lang}, prompt={Key}@{Version})",
            nodeId, subject, language, promptRes.Key, promptRes.Version);

        var response = await _claude.SendTextAsync(body, maxTokensOverride: 1024, ct);

        var parsed = TryParseEnrichmentJson(response);
        if (parsed is null)
        {
            _logger.LogWarning("Enrichment for {NodeId} returned unparseable JSON. Raw: {Raw}", nodeId, response);
            return null;
        }

        var patched = NodeEnrichmentPatcher.PatchEnrichment(structure.Nodes, nodeId, parsed.Summary, parsed.ConceptKeywords);
        structure.UpdateNodes(patched);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Enriched node {NodeId}: summary={SumChars} chars, keywords=[{Kws}]",
            nodeId, parsed.Summary.Length, string.Join(", ", parsed.ConceptKeywords));

        return new EnrichmentResult(
            parsed.Summary,
            parsed.ConceptKeywords,
            promptRes.Key,
            promptRes.Version,
            promptRes.Sha256,
            _claude.Model);
    }

    private static EnrichmentJson? TryParseEnrichmentJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var jsonPart = trimmed[start..(end + 1)];

        try
        {
            var parsed = JsonSerializer.Deserialize<EnrichmentJson>(jsonPart);
            if (parsed is null) return null;
            parsed.Summary ??= string.Empty;
            parsed.ConceptKeywords ??= Array.Empty<string>();
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class EnrichmentJson
    {
        [JsonPropertyName("summary")] public string Summary { get; set; } = string.Empty;
        [JsonPropertyName("concept_keywords")] public IReadOnlyList<string> ConceptKeywords { get; set; } = Array.Empty<string>();
    }
}
