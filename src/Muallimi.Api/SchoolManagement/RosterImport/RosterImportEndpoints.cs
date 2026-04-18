using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RosterImportEntity = Muallimi.Domain.SchoolManagement.RosterImport;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T058 (US2) — POST <c>/school-admin/roster/upload</c>.
///
/// Accepts a multipart/form-data upload from the school admin, persists
/// the file bytes into <see cref="IRosterFileStore"/>, writes a new
/// <see cref="RosterImport"/> row with status=<c>uploaded</c>, and
/// drives <see cref="IRosterImportWorker"/> synchronously (local parity —
/// production would enqueue a worker message here and return 202). The
/// endpoint returns the import id and initial counts so the frontend can
/// start polling the status endpoint immediately.
/// </summary>
public static class RosterImportEndpoints
{
    public const string UploadRoute = "/api/school-admin/roster/upload";

    public static IEndpointRouteBuilder MapRosterImportUpload(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(UploadRoute, HandleUploadAsync)
            .WithName("UploadRoster")
            .WithTags("SchoolManagement")
            .DisableAntiforgery();
        return routes;
    }

    public static async Task<IResult> HandleUploadAsync(
        HttpContext http,
        IFormFile file,
        IRosterImportRepository repo,
        IRosterFileStore files,
        IRosterImportWorker worker,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetSchoolAdminId(http, out var adminId))
            return Results.Unauthorized();
        if (!SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId))
            return Results.Unauthorized();

        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "empty_roster_file" });

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        var correlationId = SchoolManagementHeaders.ResolveCorrelationId(http);
        var rosterImportId = Guid.NewGuid();
        var blobKey = $"roster-imports/{rosterImportId}/{file.FileName}";

        await files.WriteAsync(blobKey, bytes, ct);

        var row = new RosterImportEntity
        {
            RosterImportId = rosterImportId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UploadedByAdminId = adminId,
            SourceFileBlobKey = blobKey,
            OriginalFileName = file.FileName,
            TotalRowCount = 0,
            SuccessCount = 0,
            ErrorCount = 0,
            SkipCount = 0,
            Status = "uploaded",
            CreatedAt = DateTime.UtcNow,
        };
        await repo.AddAsync(row, ct);
        await repo.SaveChangesAsync(ct);

        // Local parity: drive the worker in-process. A real deployment
        // would publish an import message here and return 202 immediately.
        var outcome = await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blobKey, correlationId),
            ct);

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Created(
            uri: $"/api/school-admin/roster/imports/{rosterImportId}",
            value: new
            {
                roster_import_id = outcome.RosterImportId,
                status = outcome.Status,
                total_row_count = outcome.TotalRowCount,
                success_count = outcome.SuccessCount,
                error_count = outcome.ErrorCount,
                skip_count = outcome.SkipCount,
                seat_limit_reached = outcome.SeatLimitReached,
                error_report_url = outcome.ErrorReportBlobKey is null
                    ? null
                    : $"/api/school-admin/roster/imports/{rosterImportId}/errors",
                created_at = row.CreatedAt,
            });
    }
}
