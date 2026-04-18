using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T057 + T060 + T061 (US2) — roster import orchestrator.
///
/// Runs the full import pipeline against a file already persisted into the
/// <see cref="IRosterFileStore"/>: parse → validate → seat-limit enforce →
/// link/create student &amp; parent profiles → persist all mutations →
/// emit <c>roster_imported</c> downstream event in the SAME unit of work.
///
/// Local parity: the production worker would be triggered by a queue
/// message after the upload endpoint returned 202. For the local
/// walkthrough the upload endpoint calls <see cref="ProcessAsync"/>
/// in-process so the test harness stays deterministic without a broker.
/// </summary>
public sealed record RosterImportProcessInput(
    Guid TenantId,
    Guid SchoolTenantId,
    Guid RosterImportId,
    string BlobKey,
    string CorrelationId);

public sealed record RosterImportProcessOutcome(
    Guid RosterImportId,
    int TotalRowCount,
    int SuccessCount,
    int ErrorCount,
    int SkipCount,
    string Status,
    string? ErrorReportBlobKey,
    bool SeatLimitReached);

public interface IRosterImportWorker
{
    Task<RosterImportProcessOutcome> ProcessAsync(
        RosterImportProcessInput input,
        CancellationToken ct = default);
}

public sealed class RosterImportWorker : IRosterImportWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IRosterImportRepository _imports;
    private readonly IRosterFileStore _fileStore;
    private readonly IRosterFileParser _parser;
    private readonly IRosterRowValidator _validator;
    private readonly IStudentProfileLinker _linker;
    private readonly IPhase5DownstreamEventOutbox _outbox;
    private readonly MuallimiDbContext _db;

    public RosterImportWorker(
        IRosterImportRepository imports,
        IRosterFileStore fileStore,
        IRosterFileParser parser,
        IRosterRowValidator validator,
        IStudentProfileLinker linker,
        IPhase5DownstreamEventOutbox outbox,
        MuallimiDbContext db)
    {
        _imports = imports;
        _fileStore = fileStore;
        _parser = parser;
        _validator = validator;
        _linker = linker;
        _outbox = outbox;
        _db = db;
    }

    public async Task<RosterImportProcessOutcome> ProcessAsync(
        RosterImportProcessInput input,
        CancellationToken ct = default)
    {
        var row = await _imports.GetByIdAsync(input.TenantId, input.SchoolTenantId, input.RosterImportId, ct)
            ?? throw new InvalidOperationException($"roster_import_not_found:{input.RosterImportId}");

        // Look up the school tenant so we know the grade-range and curriculum.
        var school = await _db.SchoolTenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                s => s.TenantId == input.TenantId && s.SchoolTenantId == input.SchoolTenantId,
                ct)
            ?? throw new InvalidOperationException($"school_tenant_not_found:{input.SchoolTenantId}");

        // Track attach the entity so we can mutate + persist.
        _db.RosterImports.Attach(row);
        row.Status = "validating";
        row.StartedAt = DateTime.UtcNow;

        await using var content = await _fileStore.OpenAsync(input.BlobKey, ct);
        var parseResult = _parser.Parse(content, row.OriginalFileName);

        RosterValidationResult validation;
        if (parseResult.HeaderErrors.Count > 0)
        {
            validation = new RosterValidationResult(
                Array.Empty<ValidatedRosterRow>(),
                parseResult.Rows
                    .Select(p => new ValidatedRosterRow(p, 0, string.Empty, parseResult.HeaderErrors))
                    .ToList(),
                Array.Empty<ValidatedRosterRow>());
        }
        else
        {
            validation = _validator.Validate(
                parseResult.Rows,
                school.GradeRangeStart,
                school.GradeRangeEnd);
        }

        // T061 — seat-limit enforcement. Read the live license; if the
        // number of valid rows would push seats_used past seat_limit, trim
        // the tail of valid rows into skipped and flag the import.
        var license = await _db.SchoolLicenses
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                l => l.TenantId == input.TenantId && l.SchoolTenantId == input.SchoolTenantId,
                ct);

        var seatLimitReached = false;
        var seatAllowed = validation.ValidRows.Count;
        var seatSkipped = new List<ValidatedRosterRow>();
        if (license is not null && license.SeatLimit > 0)
        {
            var remaining = Math.Max(0, license.SeatLimit - license.SeatsUsed);
            if (validation.ValidRows.Count > remaining)
            {
                seatLimitReached = true;
                seatAllowed = remaining;
                seatSkipped = validation.ValidRows.Skip(remaining).ToList();
            }
        }

        var processable = validation.ValidRows.Take(seatAllowed).ToList();
        var skippedCombined = validation.SkippedDuplicates.Concat(seatSkipped).ToList();

        var linkOutcomes = new List<RosterLinkOutcome>(processable.Count);
        foreach (var validRow in processable)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await _linker.LinkAsync(
                input.TenantId,
                input.SchoolTenantId,
                school.CurriculumType,
                validRow,
                ct);
            linkOutcomes.Add(outcome);
        }

        var newStudentCount = linkOutcomes.Count(o => !o.StudentExisted);
        if (license is not null && newStudentCount > 0)
        {
            _db.SchoolLicenses.Attach(license);
            license.SeatsUsed += newStudentCount;
            license.UpdatedAt = DateTime.UtcNow;
        }

        // Build the error report blob if there were rejected rows. Skipped
        // rows (in-file duplicates + seat-limit trims) are reported in a
        // dedicated "skip" section.
        string? errorReportKey = null;
        if (validation.RejectedRows.Count > 0 || skippedCombined.Count > 0)
        {
            errorReportKey = $"error-reports/{input.RosterImportId}.csv";
            var report = BuildErrorReport(validation.RejectedRows, skippedCombined, seatSkipped);
            await _fileStore.WriteAsync(errorReportKey, report, ct);
        }

        row.TotalRowCount = parseResult.Rows.Count;
        row.SuccessCount = processable.Count;
        row.ErrorCount = validation.RejectedRows.Count;
        row.SkipCount = skippedCombined.Count;
        row.ErrorReportBlobKey = errorReportKey;
        row.Status = validation.RejectedRows.Count == parseResult.Rows.Count && parseResult.Rows.Count > 0
            ? "failed"
            : "completed";
        row.CompletedAt = DateTime.UtcNow;

        // T060 — emit roster_imported into the downstream-event outbox in
        // the SAME unit of work.
        await _outbox.EnqueueAsync(
            Phase5DownstreamEventKind.roster_imported,
            tenantId: input.TenantId,
            schoolTenantId: input.SchoolTenantId,
            payload: new
            {
                roster_import_id = row.RosterImportId,
                total_row_count = row.TotalRowCount,
                success_count = row.SuccessCount,
                error_count = row.ErrorCount,
                skip_count = row.SkipCount,
                seat_limit_reached = seatLimitReached,
                new_student_count = newStudentCount,
                status = row.Status,
                completed_at = row.CompletedAt,
            },
            correlationId: input.CorrelationId,
            occurredAt: row.CompletedAt,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        return new RosterImportProcessOutcome(
            RosterImportId: row.RosterImportId,
            TotalRowCount: row.TotalRowCount,
            SuccessCount: row.SuccessCount,
            ErrorCount: row.ErrorCount,
            SkipCount: row.SkipCount,
            Status: row.Status,
            ErrorReportBlobKey: row.ErrorReportBlobKey,
            SeatLimitReached: seatLimitReached);
    }

    private static byte[] BuildErrorReport(
        IReadOnlyList<ValidatedRosterRow> rejected,
        IReadOnlyList<ValidatedRosterRow> skipped,
        IReadOnlyList<ValidatedRosterRow> seatSkipped)
    {
        var sb = new StringBuilder();
        sb.AppendLine("row_number,outcome,student_name_ar,student_name_en,grade,parent_email,reasons");
        foreach (var r in rejected)
        {
            AppendReportRow(sb, r, "rejected", string.Join("|", r.Errors));
        }
        foreach (var s in skipped)
        {
            var reason = seatSkipped.Contains(s) ? "skipped:seat_limit" : "skipped:duplicate_in_file";
            AppendReportRow(sb, s, "skipped", reason);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void AppendReportRow(StringBuilder sb, ValidatedRosterRow row, string outcome, string reasons)
    {
        sb.Append(row.Source.RowNumber).Append(',');
        sb.Append(outcome).Append(',');
        sb.Append(Escape(row.Source.StudentNameAr)).Append(',');
        sb.Append(Escape(row.Source.StudentNameEn)).Append(',');
        sb.Append(row.Source.Grade).Append(',');
        sb.Append(Escape(row.Source.ParentEmail)).Append(',');
        sb.Append(Escape(reasons)).AppendLine();
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }
}

public static class RosterImportWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5RosterImportWorker(this IServiceCollection services)
    {
        services.AddScoped<IRosterImportWorker, RosterImportWorker>();
        return services;
    }
}
