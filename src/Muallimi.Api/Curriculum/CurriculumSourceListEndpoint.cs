using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Content;
using Muallimi.Domain.Curriculum;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Curriculum;

public static class CurriculumSourceListEndpoint
{
    public static IEndpointRouteBuilder MapCurriculumSourceList(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/admin/curriculum/sources", async (MuallimiDbContext db) =>
        {
            var sources = await db.CurriculumSources
                .AsNoTracking()
                .OrderByDescending(s => s.UploadedAt)
                .Take(200)
                .ToListAsync();

            var sourceIds = sources.Select(s => s.SourceId).ToList();
            var jobs = await db.IngestionJobs
                .AsNoTracking()
                .Where(j => sourceIds.Contains(j.SourceId))
                .ToListAsync();

            var latestJobBySource = jobs
                .GroupBy(j => j.SourceId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.StartedAt).First());

            var result = sources.Select(s =>
            {
                latestJobBySource.TryGetValue(s.SourceId, out var job);
                return new
                {
                    source_id = s.SourceId,
                    curriculum_type = s.CurriculumType.ToString(),
                    grade = s.Grade.ToString(),
                    subject = s.Subject.ToString(),
                    academic_year = s.AcademicYear,
                    tutor_language = s.TutorLanguage.ToString(),
                    file_format = s.FileFormat.ToString(),
                    storage_key = s.StorageKey,
                    original_file_name = s.OriginalFileName,
                    uploaded_at = s.UploadedAt,
                    status = s.Status.ToString(),
                    job_id = (Guid?)(job?.JobId),
                    job_status = job?.Status.ToString(),
                };
            });

            return Results.Ok(result);
        })
        .WithName("ListCurriculumSourcesV2")
        .WithTags("Curriculum");

        return routes;
    }
}
