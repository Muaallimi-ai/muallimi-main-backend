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
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Curriculum;

/// <summary>
/// Aggregates the full curriculum pipeline for a single source — ingestion stages,
/// per-lesson generation rollup, and per-asset review rollup — so the curriculum-admin
/// tracker can render a 7-step progress timeline with green/yellow/red lights.
/// </summary>
public static class CurriculumSourcePipelineEndpoint
{
    public static IEndpointRouteBuilder MapCurriculumSourcePipeline(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/admin/curriculum/sources/{sourceId:guid}/pipeline", async (
            Guid sourceId, MuallimiDbContext db) =>
        {
            var source = await db.CurriculumSources
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SourceId == sourceId);
            if (source is null)
                return Results.NotFound(new { error = "Source not found." });

            var ingestionJob = await db.IngestionJobs
                .AsNoTracking()
                .Where(j => j.SourceId == sourceId)
                .OrderByDescending(j => j.StartedAt)
                .FirstOrDefaultAsync();

            JsonNode? ingestionStages = null;
            if (ingestionJob is not null)
            {
                try { ingestionStages = JsonNode.Parse(ingestionJob.Stages); }
                catch (JsonException) { ingestionStages = null; }
            }

            var structure = await db.CurriculumStructures
                .AsNoTracking()
                .Where(s => s.SourceId == sourceId)
                .OrderByDescending(s => s.ExtractedAt)
                .FirstOrDefaultAsync();

            var lessonIds = new List<Guid>();
            int totalLessons = 0;
            if (structure is not null)
            {
                lessonIds = await db.Lessons
                    .AsNoTracking()
                    .Where(l => l.StructureId == structure.StructureId)
                    .Select(l => l.LessonId)
                    .ToListAsync();
                totalLessons = lessonIds.Count;
            }

            var genJobs = lessonIds.Count > 0
                ? await db.GenerationJobs
                    .AsNoTracking()
                    .Where(g => lessonIds.Contains(g.LessonId))
                    .Select(g => new { g.LessonId, g.Status })
                    .ToListAsync()
                : new();

            var latestGenByLesson = genJobs
                .GroupBy(g => g.LessonId)
                .ToDictionary(grp => grp.Key, grp => grp.First().Status);

            int genCompleted = latestGenByLesson.Values.Count(s => s == GenerationJobStatus.Completed);
            int genRunning   = latestGenByLesson.Values.Count(s => s == GenerationJobStatus.Running);
            int genQueued    = latestGenByLesson.Values.Count(s => s == GenerationJobStatus.Queued);
            int genFailed    = latestGenByLesson.Values.Count(s => s == GenerationJobStatus.Failed
                                                              || s == GenerationJobStatus.PartialFailed);
            int genNotStarted = totalLessons - latestGenByLesson.Count;

            var assets = lessonIds.Count > 0
                ? await db.GeneratedAssets
                    .AsNoTracking()
                    .Where(a => lessonIds.Contains(a.LessonId))
                    .Select(a => a.Status)
                    .ToListAsync()
                : new();

            int aTotal           = assets.Count;
            int aQueued          = assets.Count(s => s == AssetStatus.Queued);
            int aProducing       = assets.Count(s => s == AssetStatus.Producing);
            int aAutoValidating  = assets.Count(s => s == AssetStatus.AutoValidating);
            int aAutoFailed      = assets.Count(s => s == AssetStatus.AutoFailed);
            int aPendingAdmin    = assets.Count(s => s == AssetStatus.PendingAdminReview);
            int aPendingExpert   = assets.Count(s => s == AssetStatus.PendingExpertReview);
            int aApproved        = assets.Count(s => s == AssetStatus.Approved);
            int aRejected        = assets.Count(s => s == AssetStatus.Rejected);
            int aEditRequested   = assets.Count(s => s == AssetStatus.EditRequested);

            int aPastAuto        = aPendingAdmin + aPendingExpert + aApproved + aRejected + aEditRequested;
            int aPastAdmin       = aPendingExpert + aApproved + aRejected + aEditRequested;

            string ingestionStatus = ingestionJob?.Status.ToString() ?? "NotStarted";

            string genRollup;
            if (totalLessons == 0) genRollup = "NotStarted";
            else if (genFailed > 0) genRollup = "Failed";
            else if (genCompleted == totalLessons) genRollup = "Completed";
            else if (genRunning + genQueued + genNotStarted > 0) genRollup = "Running";
            else genRollup = "Running";

            string autoRollup;
            if (aTotal == 0) autoRollup = "NotStarted";
            else if (aAutoFailed > 0) autoRollup = "Failed";
            else if (aPastAuto == aTotal) autoRollup = "Completed";
            else if (aQueued + aProducing + aAutoValidating > 0) autoRollup = "Running";
            else autoRollup = "Running";

            string adminRollup;
            if (aTotal == 0) adminRollup = "NotStarted";
            else if (aRejected + aEditRequested > 0) adminRollup = "Failed";
            else if (aPastAdmin == aTotal) adminRollup = "Completed";
            else if (aPendingAdmin > 0) adminRollup = "Running";
            else adminRollup = "Running";

            string expertRollup;
            if (aTotal == 0) expertRollup = "NotStarted";
            else if (aRejected + aEditRequested > 0) expertRollup = "Failed";
            else if (aApproved == aTotal) expertRollup = "Completed";
            else if (aPendingExpert > 0) expertRollup = "Running";
            else expertRollup = "Running";

            string publishedRollup =
                (aTotal > 0 && aApproved == aTotal) ? "Completed" : "NotStarted";

            return Results.Ok(new
            {
                source_id = source.SourceId,
                curriculum_type = source.CurriculumType.ToString(),
                grade = source.Grade.ToString(),
                subject = source.Subject.ToString(),
                academic_year = source.AcademicYear,
                tutor_language = source.TutorLanguage.ToString(),
                file_format = source.FileFormat.ToString(),
                storage_key = source.StorageKey,
                original_file_name = source.OriginalFileName,
                uploaded_at = source.UploadedAt,
                source_status = source.Status.ToString(),
                upload = new
                {
                    status = "Completed",
                    completed_at = (DateTime?)source.UploadedAt,
                },
                ingestion = new
                {
                    job_id = (Guid?)(ingestionJob?.JobId),
                    status = ingestionStatus,
                    started_at = ingestionJob?.StartedAt,
                    completed_at = ingestionJob?.CompletedAt,
                    error_reason = ingestionJob?.ErrorReason,
                    stages = ingestionStages,
                },
                generation = new
                {
                    status = genRollup,
                    total_lessons = totalLessons,
                    completed = genCompleted,
                    running = genRunning,
                    queued = genQueued,
                    failed = genFailed,
                    not_started = genNotStarted,
                },
                auto_validation = new
                {
                    status = autoRollup,
                    total_assets = aTotal,
                    past_auto_validation = aPastAuto,
                    auto_failed = aAutoFailed,
                    in_progress = aQueued + aProducing + aAutoValidating,
                },
                admin_review = new
                {
                    status = adminRollup,
                    total_assets = aTotal,
                    past_admin = aPastAdmin,
                    pending = aPendingAdmin,
                    rejected = aRejected,
                    edit_requested = aEditRequested,
                },
                expert_review = new
                {
                    status = expertRollup,
                    total_assets = aTotal,
                    approved = aApproved,
                    pending = aPendingExpert,
                    rejected = aRejected,
                    edit_requested = aEditRequested,
                },
                published = new
                {
                    status = publishedRollup,
                    approved_assets = aApproved,
                    total_assets = aTotal,
                },
            });
        })
        .WithName("GetCurriculumSourcePipeline")
        .WithTags("Curriculum");

        return routes;
    }
}
