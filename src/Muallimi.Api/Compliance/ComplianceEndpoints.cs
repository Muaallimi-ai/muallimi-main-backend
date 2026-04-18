using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.DataDeletion;
using Muallimi.Api.Compliance.DataExport;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Compliance;

/// <summary>
/// T094 + T095 — Data rights endpoints per security-data-protection-contract.md:
/// data-access requests, data export download, data-deletion requests, deletion
/// status, and the static data processing register.
/// </summary>
public static class ComplianceEndpoints
{
    public static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/compliance/data-access-request", async (
            HttpContext http,
            DataAccessRequestInput input,
            IDataExportService export,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(http, out var tenantId))
                return Results.BadRequest(new { error = "missing_tenant" });
            var correlationId = ResolveCorrelationId(http);
            var requestedBy = ResolveActorId(http);

            var archive = await export.GenerateAsync(
                tenantId, input.TargetScope, input.TargetId, requestedBy, correlationId, ct);

            return Results.Ok(new
            {
                request_id = Guid.NewGuid(),
                status = "ready",
                estimated_completion = DateTime.UtcNow,
                file_name = archive.FileName,
                entries = archive.Entries,
                file_size_bytes = archive.ZipBytes.Length,
            });
        });

        routes.MapGet("/api/v1/compliance/data-access-request/{requestId:guid}/export", async (
            Guid requestId,
            HttpContext http,
            string? target_scope,
            Guid? target_id,
            IDataExportService export,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(http, out var tenantId))
                return Results.BadRequest(new { error = "missing_tenant" });
            if (string.IsNullOrEmpty(target_scope) || target_id is null)
                return Results.BadRequest(new { error = "missing_target" });

            var correlationId = ResolveCorrelationId(http);
            var requestedBy = ResolveActorId(http);
            var archive = await export.GenerateAsync(
                tenantId, target_scope!, target_id!.Value, requestedBy, correlationId, ct);

            return Results.File(archive.ZipBytes, archive.ContentType, archive.FileName);
        });

        routes.MapPost("/api/v1/compliance/data-deletion-request", async (
            HttpContext http,
            DataDeletionRequestInput input,
            IDataDeletionService deletion,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(http, out var tenantId))
                return Results.BadRequest(new { error = "missing_tenant" });
            if (!input.Acknowledgement)
                return Results.BadRequest(new { error = "acknowledgement_required" });

            var correlationId = ResolveCorrelationId(http);
            var requestedBy = ResolveActorId(http);
            var request = await deletion.CreateAsync(
                tenantId, input.TargetScope, input.TargetId, requestedBy, correlationId, ct);

            return Results.Ok(new
            {
                deletion_request_id = request.DeletionRequestId,
                status = request.Status,
                tables_to_process = new object[]
                {
                    new { table_name = "payment_transactions", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "invoices", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "subscriptions", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "exam_submissions", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "leaderboard_snapshots", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "weekly_reports", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "badge_awards", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "mastery_states", estimated_rows = 0, action = "anonymise" },
                    new { table_name = "session_events", estimated_rows = 0, action = "delete" },
                    new { table_name = "student_profiles", estimated_rows = 0, action = "anonymise" },
                },
                retention_window_days = 30,
            });
        });

        routes.MapGet("/api/v1/compliance/data-deletion-request/{requestId:guid}", async (
            Guid requestId,
            HttpContext http,
            MuallimiDbContext db,
            CancellationToken ct) =>
        {
            if (!TryGetTenantId(http, out var tenantId))
                return Results.BadRequest(new { error = "missing_tenant" });

            var request = await db.DataDeletionRequests
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId && r.DeletionRequestId == requestId)
                .FirstOrDefaultAsync(ct);
            if (request is null) return Results.NotFound();

            return Results.Ok(new
            {
                deletion_request_id = request.DeletionRequestId,
                status = request.Status,
                tables_processed = ParseTables(request.TablesProcessed),
                completed_at = request.CompletedAt,
                confirmation_sent = request.ConfirmationSentAt.HasValue,
            });
        });

        routes.MapGet("/api/v1/compliance/data-register", () =>
            Results.Ok(DataProcessingRegister.DataProcessingRegister.GetRegister()));

        return routes;
    }

    private static bool TryGetTenantId(HttpContext http, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        var header = http.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        return Guid.TryParse(header, out tenantId);
    }

    private static string ResolveCorrelationId(HttpContext http)
        => http.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString();

    private static Guid ResolveActorId(HttpContext http)
    {
        var header = http.Request.Headers["X-Actor-Id"].FirstOrDefault();
        return Guid.TryParse(header, out var id) ? id : Guid.Empty;
    }

    private static object? ParseTables(string? json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<object>();
        try { return System.Text.Json.JsonSerializer.Deserialize<object>(json); }
        catch { return json; }
    }
}

public sealed record DataAccessRequestInput(
    string TargetScope,
    Guid TargetId,
    string? Reason);

public sealed record DataDeletionRequestInput(
    string TargetScope,
    Guid TargetId,
    string? Reason,
    bool Acknowledgement);
