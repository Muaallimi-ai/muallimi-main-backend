using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Curriculum;

public static class CurriculumNodeChunksEndpoint
{
    public static IEndpointRouteBuilder MapCurriculumNodeChunks(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/admin/curriculum/{sourceId:guid}/nodes/{nodeId}/chunks", async (
            Guid sourceId, string nodeId, MuallimiDbContext db) =>
        {
            var structure = await db.CurriculumStructures
                .AsNoTracking()
                .Where(s => s.SourceId == sourceId)
                .OrderByDescending(s => s.ExtractedAt)
                .FirstOrDefaultAsync();

            if (structure is null)
                return Results.NotFound(new { error = "Structure not found for this source." });

            JsonNode? root;
            try { root = JsonNode.Parse(structure.Nodes); }
            catch (JsonException) { return Results.BadRequest(new { error = "Structure JSON is malformed." }); }

            if (root is not JsonArray rootArray)
                return Results.BadRequest(new { error = "Structure root is not an array." });

            var found = FindNode(rootArray, nodeId, new List<string>());
            if (found.Node is null)
                return Results.NotFound(new { error = "Node not found in structure tree." });

            var lessonRefs = new List<string>();
            CollectLessonPaths(found.Node, found.ParentTrail, lessonRefs);

            var nodeType = found.Node["node_type"]?.GetValue<string>();
            var nodeTitle = found.Node["title"]?.GetValue<string>();

            if (lessonRefs.Count == 0)
            {
                return Results.Ok(new
                {
                    node_id = nodeId,
                    node_type = nodeType,
                    title = nodeTitle,
                    lessons = Array.Empty<object>(),
                });
            }

            var dbLessons = await db.Lessons
                .AsNoTracking()
                .Where(l => l.StructureId == structure.StructureId && lessonRefs.Contains(l.Path))
                .ToListAsync();

            var lessonIds = dbLessons.Select(l => l.LessonId).ToList();

            var chunks = await db.ContentChunks
                .AsNoTracking()
                .Where(c => lessonIds.Contains(c.LessonId))
                .OrderBy(c => c.LessonId)
                .ThenBy(c => c.Sequence)
                .Select(c => new
                {
                    c.ChunkId,
                    c.LessonId,
                    c.Sequence,
                    c.Text,
                    c.TokenCount,
                    c.SourceRefs,
                    c.MathBlocks,
                    c.OverlapWithPrevious,
                    c.EmbeddingModelVersion,
                    Status = c.Status.ToString(),
                })
                .ToListAsync();

            var chunksByLesson = chunks
                .GroupBy(c => c.LessonId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var lessonsResponse = lessonRefs
                .Select(path =>
                {
                    var lesson = dbLessons.FirstOrDefault(l => l.Path == path);
                    if (lesson is null)
                    {
                        return new
                        {
                            lesson_id = (Guid?)null,
                            path,
                            status = "not_persisted",
                            content_hash = (string?)null,
                            chunks = Array.Empty<object>(),
                        } as object;
                    }
                    var lessonChunks = chunksByLesson.TryGetValue(lesson.LessonId, out var cl) ? cl : new();
                    return new
                    {
                        lesson_id = (Guid?)lesson.LessonId,
                        path = lesson.Path,
                        status = lesson.Status.ToString(),
                        content_hash = (string?)lesson.ContentHash,
                        chunks = lessonChunks.Select(c => new
                        {
                            chunk_id = c.ChunkId,
                            sequence = c.Sequence,
                            text = c.Text,
                            token_count = c.TokenCount,
                            overlap_with_previous = c.OverlapWithPrevious,
                            source_refs = ParseJsonArray(c.SourceRefs),
                            math_blocks = ParseJsonArray(c.MathBlocks),
                            embedding_model = c.EmbeddingModelVersion,
                            status = c.Status,
                        }).Cast<object>().ToArray(),
                    } as object;
                })
                .ToList();

            return Results.Ok(new
            {
                node_id = nodeId,
                node_type = nodeType,
                title = nodeTitle,
                lessons = lessonsResponse,
            });
        })
        .WithName("GetCurriculumNodeChunks")
        .WithTags("Curriculum");

        return routes;
    }

    private static (JsonNode? Node, List<string> ParentTrail) FindNode(
        JsonArray nodes, string nodeId, List<string> trail)
    {
        foreach (var n in nodes)
        {
            if (n is null) continue;
            var id = n["node_id"]?.GetValue<string>();
            var title = n["title"]?.GetValue<string>() ?? string.Empty;

            if (string.Equals(id, nodeId, StringComparison.OrdinalIgnoreCase))
                return (n, trail);

            if (n["children"] is JsonArray children && children.Count > 0)
            {
                var nextTrail = new List<string>(trail) { title };
                var sub = FindNode(children, nodeId, nextTrail);
                if (sub.Node is not null) return sub;
            }
        }
        return (null, new List<string>());
    }

    private static void CollectLessonPaths(JsonNode node, List<string> parentTrail, List<string> acc)
    {
        var title = node["title"]?.GetValue<string>() ?? string.Empty;
        var nodeType = node["node_type"]?.GetValue<string>();
        var pathTrail = new List<string>(parentTrail) { title };

        if (string.Equals(nodeType, "lesson", StringComparison.OrdinalIgnoreCase))
        {
            acc.Add(string.Join(" > ", pathTrail));
            return;
        }

        if (node["children"] is JsonArray children)
        {
            foreach (var c in children)
            {
                if (c is not null) CollectLessonPaths(c, pathTrail, acc);
            }
        }
    }

    private static JsonNode? ParseJsonArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonNode.Parse(raw); }
        catch (JsonException) { return null; }
    }
}
