using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Muallimi.Api.RetrievalApi;

/// <summary>
/// T093 - Pgvector scope-aware query service for runtime retrieval.
/// Filters by curriculum type, grade, subject, tutor language, and optionally active lesson.
/// Returns only chunks with status=Active whose lessons have status=Approved.
/// </summary>
public class ChunkRetrievalService
{
    private readonly MuallimiDbContext _db;

    public ChunkRetrievalService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<List<ChunkResult>> RetrieveChunksAsync(
        Vector queryEmbedding,
        CurriculumType curriculumType,
        Grade grade,
        Subject subject,
        TutorLanguage tutorLanguage,
        Guid? activeLessonId,
        int maxChunks = 5,
        CancellationToken ct = default)
    {
        // Build the base query: only active chunks from approved lessons
        var query = _db.ContentChunks
            .Where(c => c.Status == ChunkStatus.Active)
            .Where(c => c.Embedding != null)
            .Join(
                _db.Lessons.Where(l => l.Status == LessonStatus.Approved
                                       && l.CurriculumType == curriculumType
                                       && l.Grade == grade
                                       && l.Subject == subject
                                       && l.TutorLanguage == tutorLanguage),
                chunk => chunk.LessonId,
                lesson => lesson.LessonId,
                (chunk, lesson) => new { chunk, lesson });

        // Bias toward active lesson if provided
        if (activeLessonId.HasValue)
        {
            query = query.Where(x => x.lesson.LessonId == activeLessonId.Value);
        }

        var results = await query
            .OrderBy(x => x.chunk.Embedding!.CosineDistance(queryEmbedding))
            .Take(maxChunks)
            .Select(x => new ChunkResult
            {
                ChunkId = x.chunk.ChunkId,
                Text = x.chunk.Text,
                LessonId = x.lesson.LessonId,
                Topic = x.lesson.Path,
                Metadata = x.chunk.Metadata,
                SourceRefs = x.chunk.SourceRefs,
                CosineDistance = x.chunk.Embedding!.CosineDistance(queryEmbedding)
            })
            .ToListAsync(ct);

        // Convert cosine distance to similarity (confidence)
        foreach (var r in results)
        {
            r.Confidence = 1.0 - r.CosineDistance;
        }

        return results;
    }
}

public class ChunkResult
{
    public Guid ChunkId { get; set; }
    public string Text { get; set; } = string.Empty;
    public Guid LessonId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Metadata { get; set; } = "{}";
    public string SourceRefs { get; set; } = "[]";
    public double CosineDistance { get; set; }
    public double Confidence { get; set; }
}
