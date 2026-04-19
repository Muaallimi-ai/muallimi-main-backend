using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Audit;
using Muallimi.Infrastructure.BlobStorage;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Curriculum;

/// <summary>
/// DELETE /admin/curriculum/sources/{sourceId}
///
/// Cascades through every table that hangs off a CurriculumSource so the
/// curriculum admin can fully remove a botched/legacy upload from the tracker
/// (and its blob from MinIO). The order here matters — children are deleted
/// before their parents to keep the FK graph consistent.
/// </summary>
public static class CurriculumSourceDeleteEndpoint
{
    public static IEndpointRouteBuilder MapCurriculumSourceDelete(this IEndpointRouteBuilder routes)
    {
        routes.MapDelete("/admin/curriculum/sources/{sourceId:guid}", async (
            Guid sourceId,
            MuallimiDbContext db,
            ICurriculumBlobStore blobStore,
            AuditEventEmitter audit,
            ILoggerFactory loggerFactory,
            HttpContext httpContext) =>
        {
            var logger = loggerFactory.CreateLogger("CurriculumSourceDelete");
            var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
            var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";

            var source = await db.CurriculumSources.FindAsync(new object[] { sourceId }, httpContext.RequestAborted);
            if (source is null)
                return Results.NotFound(new { error = "Source not found." });

            var storageKey = source.StorageKey;

            // Resolve the dependency graph before mutating anything.
            var structureIds = await db.CurriculumStructures
                .Where(s => s.SourceId == sourceId)
                .Select(s => s.StructureId)
                .ToListAsync(httpContext.RequestAborted);

            var lessonIds = structureIds.Count == 0
                ? new System.Collections.Generic.List<Guid>()
                : await db.Lessons
                    .Where(l => structureIds.Contains(l.StructureId))
                    .Select(l => l.LessonId)
                    .ToListAsync(httpContext.RequestAborted);

            var assetIds = lessonIds.Count == 0
                ? new System.Collections.Generic.List<Guid>()
                : await db.GeneratedAssets
                    .Where(a => lessonIds.Contains(a.LessonId))
                    .Select(a => a.AssetId)
                    .ToListAsync(httpContext.RequestAborted);

            // Cascade — leaves first, then trunks. ExecuteDeleteAsync issues a
            // single SQL DELETE per table without round-tripping rows.
            using var tx = await db.Database.BeginTransactionAsync(httpContext.RequestAborted);
            try
            {
                if (assetIds.Count > 0)
                {
                    await db.PublishedAssets.Where(p => lessonIds.Contains(p.LessonId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.ReviewDecisions.Where(r => assetIds.Contains(r.AssetId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.ReviewAssignments.Where(r => assetIds.Contains(r.AssetId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.AutoValidationResults.Where(v => assetIds.Contains(v.AssetId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.GeneratedAssets.Where(a => assetIds.Contains(a.AssetId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                }

                if (lessonIds.Count > 0)
                {
                    await db.GenerationJobs.Where(j => lessonIds.Contains(j.LessonId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.FormatDecisions.Where(f => lessonIds.Contains(f.LessonId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.ChangeLogEntries.Where(c => lessonIds.Contains(c.LessonId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.ContentChunks.Where(c => lessonIds.Contains(c.LessonId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                    await db.Lessons.Where(l => lessonIds.Contains(l.LessonId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                }

                if (structureIds.Count > 0)
                {
                    await db.CurriculumStructures.Where(s => structureIds.Contains(s.StructureId)).ExecuteDeleteAsync(httpContext.RequestAborted);
                }

                await db.IngestionJobs.Where(j => j.SourceId == sourceId).ExecuteDeleteAsync(httpContext.RequestAborted);
                await db.CurriculumSources.Where(s => s.SourceId == sourceId).ExecuteDeleteAsync(httpContext.RequestAborted);

                await tx.CommitAsync(httpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(httpContext.RequestAborted);
                logger.LogError(ex, "Failed to delete curriculum source {SourceId}", sourceId);
                return Results.Problem(
                    title: "Failed to delete source.",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            // Best-effort blob cleanup — already committed, so a failure here
            // just leaves an orphan object behind (logged inside the store).
            if (!string.IsNullOrWhiteSpace(storageKey))
                await blobStore.DeleteAsync(storageKey, httpContext.RequestAborted);

            audit.Emit(new AuditEvent
            {
                EventCategory = "curriculum",
                Action = "delete",
                TargetType = "CurriculumSource",
                TargetId = sourceId.ToString(),
                ActorId = actor,
                TenantId = httpContext.Items["TenantId"]?.ToString() ?? "local",
                Outcome = "succeeded",
                CorrelationId = correlationId
            });

            return Results.Ok(new
            {
                source_id = sourceId,
                deleted_lessons = lessonIds.Count,
                deleted_assets = assetIds.Count,
                deleted_structures = structureIds.Count,
                correlation_id = correlationId
            });
        })
        .WithName("DeleteCurriculumSource")
        .WithTags("Curriculum");

        return routes;
    }
}
