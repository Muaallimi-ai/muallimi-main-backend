using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Publication;
using Muallimi.Application.Audit;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;
using Pgvector;

namespace Muallimi.Api.RetrievalApi;

/// <summary>
/// T096 + T097 - Runtime retrieval endpoints for serving approved content
/// to the learning engine. These are lookup-only — no generation is ever triggered.
/// </summary>
public static class RetrievalEndpoints
{
    public static void MapRetrievalEndpoints(this WebApplication app)
    {
        // ── T096: POST /internal/content/retrieve ──
        app.MapPost("/internal/content/retrieve", async (
            RetrieveRequest request,
            HttpContext httpContext,
            MuallimiDbContext db,
            ChunkRetrievalService chunkService,
            QaCacheLookupService qaCacheService,
            LogicalCdnUrlProvider cdnUrlProvider,
            FallbackModeProjection fallbackProjection,
            AuditEventEmitter audit) =>
        {
            var correlationId = httpContext.Items["CorrelationId"]?.ToString()
                                ?? request.CorrelationId
                                ?? Guid.NewGuid().ToString();

            // Propagate correlation ID in response header
            httpContext.Response.Headers["X-Correlation-Id"] = correlationId;

            // Parse scope enums
            if (!Enum.TryParse<CurriculumType>(request.Scope.CurriculumType, ignoreCase: true, out var ctEnum))
                return Results.BadRequest(new { error = $"Invalid curriculum_type '{request.Scope.CurriculumType}'." });
            if (!Enum.TryParse<Grade>(request.Scope.Grade, ignoreCase: true, out var gradeEnum))
                return Results.BadRequest(new { error = $"Invalid grade '{request.Scope.Grade}'." });
            if (!Enum.TryParse<Subject>(request.Scope.Subject, ignoreCase: true, out var subjectEnum))
                return Results.BadRequest(new { error = $"Invalid subject '{request.Scope.Subject}'." });
            if (!Enum.TryParse<TutorLanguage>(request.Scope.TutorLanguage, ignoreCase: true, out var langEnum))
                return Results.BadRequest(new { error = $"Invalid tutor_language '{request.Scope.TutorLanguage}'." });

            // Lookup-only guard: snapshot before
            var lookupGuard = new LookupOnlyGuard(db);
            await lookupGuard.SnapshotBeforeAsync();

            Guid? activeLessonId = null;
            if (!string.IsNullOrEmpty(request.Scope.ActiveLessonId)
                && Guid.TryParse(request.Scope.ActiveLessonId, out var parsedLessonId))
            {
                activeLessonId = parsedLessonId;
            }

            var maxChunks = request.MaxChunks > 0 ? request.MaxChunks : 5;

            // We need a query embedding. In production the ai-service provides it.
            // For the runtime retrieval contract we expect the caller to send query_text,
            // and we use a placeholder embedding lookup. The actual embedding generation
            // is done by ai-service before calling this endpoint, passing the vector.
            // For now, we support two modes:
            // 1. query_embedding provided directly (ai-service pre-computed)
            // 2. query_text only (fallback: search by text similarity — not vector)

            List<ChunkResult> chunks;
            if (request.QueryEmbedding is not null && request.QueryEmbedding.Length > 0)
            {
                var queryVector = new Vector(request.QueryEmbedding);
                chunks = await chunkService.RetrieveChunksAsync(
                    queryVector, ctEnum, gradeEnum, subjectEnum, langEnum,
                    activeLessonId, maxChunks);
            }
            else
            {
                // Fallback: text-based lookup (returns chunks from the active lesson)
                chunks = await FallbackTextRetrieveAsync(db, ctEnum, gradeEnum, subjectEnum, langEnum, activeLessonId, maxChunks);
            }

            // Scope isolation check
            ScopeIsolationFilter.AssertChunksInScope(chunks, ctEnum, gradeEnum, subjectEnum);

            // Q&A cache lookup
            QaCacheHit? qaCacheHit = null;
            if (request.QueryEmbedding is not null && request.QueryEmbedding.Length > 0)
            {
                var queryVector = new Vector(request.QueryEmbedding);
                qaCacheHit = await qaCacheService.LookupAsync(queryVector, ctEnum, subjectEnum);
            }

            // Get distinct lesson IDs from chunks to fetch audio & visual assets
            var lessonIds = chunks.Select(c => c.LessonId).Distinct().ToList();

            // US5: Check fallback mode for matched lessons
            var fallbackStatus = await fallbackProjection.GetFallbackStatusBatchAsync(lessonIds);

            // Audio URLs for each chunk
            var audioResults = new List<object>();
            foreach (var chunk in chunks)
            {
                var audioAsset = await db.PublishedAssets
                    .Where(p => p.LessonId == chunk.LessonId
                                && p.AssetType == AssetType.Audio
                                && p.Status == PublishedAssetStatus.Active)
                    .FirstOrDefaultAsync();

                if (audioAsset is not null)
                {
                    audioResults.Add(new
                    {
                        chunk_id = chunk.ChunkId.ToString(),
                        url = cdnUrlProvider.GenerateAudioUrl(chunk.ChunkId)
                    });
                }
            }

            // Visual assets for matched lessons (suppressed for lessons in fallback mode)
            var visualResults = new List<object>();
            foreach (var lessonId in lessonIds)
            {
                // US5: Skip visuals for lessons in fallback mode
                if (fallbackStatus.TryGetValue(lessonId, out var isInFallback) && isInFallback)
                    continue;

                var visuals = await db.PublishedAssets
                    .Where(p => p.LessonId == lessonId
                                && p.AssetType == AssetType.Visual
                                && p.Status == PublishedAssetStatus.Active)
                    .ToListAsync();

                foreach (var v in visuals)
                {
                    visualResults.Add(new
                    {
                        lesson_id = v.LessonId.ToString(),
                        asset_id = v.PublishedId.ToString(),
                        format = v.VisualFormat?.ToString()?.ToLowerInvariant(),
                        url = cdnUrlProvider.GenerateUrl(v.LessonId, v.AssetType, v.PublishedId, v.VisualFormat),
                        transcript_url = cdnUrlProvider.GenerateTranscriptUrl(v.LessonId, v.PublishedId)
                    });
                }
            }

            // Lookup-only guard: assert after
            await lookupGuard.AssertNoGenerationSideEffectsAsync();

            audit.Emit(new AuditEvent
            {
                EventCategory = "retrieval",
                Action = "content-retrieved",
                TargetType = "ContentChunk",
                TargetId = $"chunks:{chunks.Count}",
                ActorId = "learning-engine",
                TenantId = httpContext.Items["TenantId"]?.ToString() ?? "system",
                Outcome = "succeeded",
                CorrelationId = correlationId
            });

            return Results.Ok(new
            {
                chunks = chunks.Select(c => new
                {
                    chunk_id = c.ChunkId.ToString(),
                    text = c.Text,
                    metadata = System.Text.Json.JsonSerializer.Deserialize<object>(c.Metadata),
                    confidence = Math.Round(c.Confidence, 4)
                }),
                qa_cache_hit = qaCacheHit is not null ? new
                {
                    entry_id = qaCacheHit.EntryId.ToString(),
                    question_text = qaCacheHit.QuestionText,
                    answer_text = qaCacheHit.AnswerText,
                    similarity = Math.Round(qaCacheHit.Similarity, 4)
                } : null,
                audio = audioResults,
                visuals = visualResults,
                fallback_lessons = fallbackStatus.Where(f => f.Value).Select(f => f.Key)
            });
        })
        .WithName("RetrieveContent")
        .WithTags("RuntimeRetrieval");

        // ── T097: GET /internal/content/visual/{lessonId} ──
        app.MapGet("/internal/content/visual/{lessonId:guid}", async (
            Guid lessonId,
            HttpContext httpContext,
            MuallimiDbContext db,
            LogicalCdnUrlProvider cdnUrlProvider) =>
        {
            var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
            httpContext.Response.Headers["X-Correlation-Id"] = correlationId;

            var visuals = await db.PublishedAssets
                .Where(p => p.LessonId == lessonId
                            && p.AssetType == AssetType.Visual
                            && p.Status == PublishedAssetStatus.Active)
                .ToListAsync();

            // Scope isolation: all returned assets must belong to the requested lesson
            ScopeIsolationFilter.AssertAssetsInScope(
                visuals.Select(v => (v.LessonId, lessonId)));

            return Results.Ok(visuals.Select(v => new
            {
                lesson_id = v.LessonId.ToString(),
                asset_id = v.PublishedId.ToString(),
                format = v.VisualFormat?.ToString()?.ToLowerInvariant(),
                url = cdnUrlProvider.GenerateUrl(v.LessonId, v.AssetType, v.PublishedId, v.VisualFormat),
                transcript_url = cdnUrlProvider.GenerateTranscriptUrl(v.LessonId, v.PublishedId)
            }));
        })
        .WithName("GetVisualAssets")
        .WithTags("RuntimeRetrieval");

        // ── T097: GET /internal/content/audio/{chunkId} ──
        app.MapGet("/internal/content/audio/{chunkId:guid}", async (
            Guid chunkId,
            HttpContext httpContext,
            MuallimiDbContext db,
            LogicalCdnUrlProvider cdnUrlProvider) =>
        {
            var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
            httpContext.Response.Headers["X-Correlation-Id"] = correlationId;

            // Find the chunk to get its lesson ID
            var chunk = await db.ContentChunks.FindAsync(chunkId);
            if (chunk is null)
                return Results.NotFound(new { error = "Chunk not found." });

            // Find active published audio for this lesson
            var audioAsset = await db.PublishedAssets
                .Where(p => p.LessonId == chunk.LessonId
                            && p.AssetType == AssetType.Audio
                            && p.Status == PublishedAssetStatus.Active)
                .FirstOrDefaultAsync();

            if (audioAsset is null)
                return Results.Ok(new { chunk_id = chunkId.ToString(), url = (string?)null });

            return Results.Ok(new
            {
                chunk_id = chunkId.ToString(),
                url = cdnUrlProvider.GenerateAudioUrl(chunkId)
            });
        })
        .WithName("GetAudioAsset")
        .WithTags("RuntimeRetrieval");
    }

    /// <summary>
    /// Fallback text-based retrieval when no embedding vector is provided.
    /// Returns active chunks from approved lessons matching the scope.
    /// </summary>
    private static async Task<List<ChunkResult>> FallbackTextRetrieveAsync(
        MuallimiDbContext db,
        CurriculumType curriculumType,
        Grade grade,
        Subject subject,
        TutorLanguage tutorLanguage,
        Guid? activeLessonId,
        int maxChunks)
    {
        var query = db.ContentChunks
            .Where(c => c.Status == ChunkStatus.Active)
            .Join(
                db.Lessons.Where(l => l.Status == LessonStatus.Approved
                                      && l.CurriculumType == curriculumType
                                      && l.Grade == grade
                                      && l.Subject == subject
                                      && l.TutorLanguage == tutorLanguage),
                chunk => chunk.LessonId,
                lesson => lesson.LessonId,
                (chunk, lesson) => new { chunk, lesson });

        if (activeLessonId.HasValue)
            query = query.Where(x => x.lesson.LessonId == activeLessonId.Value);

        var results = await query
            .OrderBy(x => x.chunk.Sequence)
            .Take(maxChunks)
            .Select(x => new ChunkResult
            {
                ChunkId = x.chunk.ChunkId,
                Text = x.chunk.Text,
                LessonId = x.lesson.LessonId,
                Topic = x.lesson.Path,
                Metadata = x.chunk.Metadata,
                SourceRefs = x.chunk.SourceRefs,
                CosineDistance = 0,
                Confidence = 1.0 // fallback: no ranking
            })
            .ToListAsync();

        return results;
    }
}

// ── Retrieval DTOs (US4) ──

public record RetrieveRequest(
    string QueryText,
    float[]? QueryEmbedding,
    RetrieveScopeDto Scope,
    int MaxChunks = 5,
    string? CorrelationId = null);

public record RetrieveScopeDto(
    string CurriculumType,
    string Grade,
    string Subject,
    string TutorLanguage,
    string? ActiveLessonId = null);
