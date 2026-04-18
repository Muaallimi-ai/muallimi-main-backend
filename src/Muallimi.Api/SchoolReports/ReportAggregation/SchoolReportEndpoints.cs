using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.SchoolManagement;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolReports.ReportExport;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.SchoolReports.ReportAggregation;

/// <summary>
/// T176 (US9) — school-admin report endpoints.
///
/// Routes:
///   • POST /api/school-admin/reports                        → create + queue
///   • GET  /api/school-admin/reports                        → list reports
///   • GET  /api/school-admin/reports/{id}                   → status + payload
///   • GET  /api/school-admin/reports/{id}/export            → PDF bytes
///
/// Generation is async: POST returns immediately with <c>status=generating</c>
/// and the background job (<see cref="SchoolReportGenerationJob"/>) picks up
/// the row. Tests can drive <c>RunOnceAsync</c> synchronously to keep the
/// contract deterministic.
/// </summary>
public static class SchoolReportEndpoints
{
    public const string CreateRoute = "/api/school-admin/reports";
    public const string ListRoute = "/api/school-admin/reports";
    public const string StatusRoute = "/api/school-admin/reports/{schoolReportId:guid}";
    public const string ExportRoute = "/api/school-admin/reports/{schoolReportId:guid}/export";

    public sealed record CreateRequest(
        string report_type,
        int? grade_filter,
        string? subject_filter,
        string? class_filter,
        DateTime window_start,
        DateTime window_end,
        string? language);

    public static IEndpointRouteBuilder MapSchoolReportAdmin(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(CreateRoute, HandleCreateAsync).WithName("CreateSchoolReport").WithTags("SchoolReports");
        routes.MapGet(ListRoute, HandleListAsync).WithName("ListSchoolReports").WithTags("SchoolReports");
        routes.MapGet(StatusRoute, HandleStatusAsync).WithName("GetSchoolReport").WithTags("SchoolReports");
        routes.MapGet(ExportRoute, HandleExportAsync).WithName("ExportSchoolReport").WithTags("SchoolReports");
        return routes;
    }

    public static async Task<IResult> HandleCreateAsync(
        HttpContext http,
        CreateRequest body,
        ISchoolReportRepository reports,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out var adminId))
            return Results.Unauthorized();

        if (body is null || string.IsNullOrWhiteSpace(body.report_type))
            return Results.BadRequest(new { error = "invalid_report_payload" });
        if (!IsValidReportType(body.report_type))
            return Results.BadRequest(new { error = "invalid_report_type" });
        if (body.window_end < body.window_start)
            return Results.BadRequest(new { error = "invalid_window" });

        Guid? subjectFilter = null;
        if (!string.IsNullOrWhiteSpace(body.subject_filter))
        {
            if (!Guid.TryParse(body.subject_filter, out var parsedSubject))
                return Results.BadRequest(new { error = "invalid_subject_filter" });
            subjectFilter = parsedSubject;
        }
        Guid? classFilter = null;
        if (!string.IsNullOrWhiteSpace(body.class_filter))
        {
            if (!Guid.TryParse(body.class_filter, out var parsedClass))
                return Results.BadRequest(new { error = "invalid_class_filter" });
            classFilter = parsedClass;
        }

        var language = string.Equals(body.language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";

        var row = new SchoolReport
        {
            SchoolReportId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            GeneratedByAdminId = adminId,
            ReportType = body.report_type,
            GradeFilter = body.grade_filter,
            SubjectFilter = subjectFilter,
            ClassFilter = classFilter,
            WindowStart = body.window_start.ToUniversalTime(),
            WindowEnd = body.window_end.ToUniversalTime(),
            Language = language,
            Status = "generating",
            CreatedAt = DateTime.UtcNow,
        };
        await reports.AddAsync(row, ct);
        await reports.SaveChangesAsync(ct);

        http.Response.Headers["X-Correlation-Id"] = SchoolManagementHeaders.ResolveCorrelationId(http);
        return Results.Created(
            uri: $"{CreateRoute}/{row.SchoolReportId}",
            value: new
            {
                school_report_id = row.SchoolReportId,
                status = row.Status,
            });
    }

    public static async Task<IResult> HandleListAsync(
        HttpContext http,
        ISchoolReportRepository reports,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out _))
            return Results.Unauthorized();

        var rows = await reports.ListForSchoolAsync(tenantId, schoolTenantId, ct);
        return Results.Ok(new
        {
            reports = rows.Select(r => new
            {
                school_report_id = r.SchoolReportId,
                report_type = r.ReportType,
                status = r.Status,
                language = r.Language,
                window_start = r.WindowStart,
                window_end = r.WindowEnd,
                created_at = r.CreatedAt,
                completed_at = r.CompletedAt,
            }),
            total_count = rows.Count,
        });
    }

    public static async Task<IResult> HandleStatusAsync(
        Guid schoolReportId,
        HttpContext http,
        ISchoolReportRepository reports,
        ISchoolReportAggregator aggregator,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out _))
            return Results.Unauthorized();

        var row = await reports.GetByIdAsync(tenantId, schoolTenantId, schoolReportId, ct);
        if (row is null) return Results.NotFound(new { error = "report_not_found" });

        if (row.Status != "ready")
        {
            return Results.Ok(new
            {
                school_report_id = row.SchoolReportId,
                report_type = row.ReportType,
                status = row.Status,
                language = row.Language,
                window_start = row.WindowStart,
                window_end = row.WindowEnd,
            });
        }

        var payload = await aggregator.AggregateAsync(row, ct);
        return Results.Ok(new
        {
            school_report_id = row.SchoolReportId,
            report_type = row.ReportType,
            status = row.Status,
            language = row.Language,
            window_start = row.WindowStart,
            window_end = row.WindowEnd,
            data = new
            {
                mastery_trends = payload.MasteryTrends.Select(m => new
                {
                    period = m.Period,
                    grade = m.Grade,
                    subject_name_ar = m.SubjectNameAr,
                    subject_name_en = m.SubjectNameEn,
                    average_mastery = m.AverageMastery,
                }),
                engagement_summary = new
                {
                    active_students = payload.EngagementSummary.ActiveStudents,
                    average_sessions_per_student = payload.EngagementSummary.AverageSessionsPerStudent,
                    streak_distribution = payload.EngagementSummary.StreakDistribution,
                },
                exam_performance = payload.ExamPerformance.Select(e => new
                {
                    exam_title_ar = e.ExamTitleAr,
                    exam_title_en = e.ExamTitleEn,
                    class_average = e.ClassAverage,
                    highest_score = e.HighestScore,
                    lowest_score = e.LowestScore,
                }),
                at_risk_distribution = payload.AtRiskDistribution.Select(a => new
                {
                    class_name_ar = a.ClassNameAr,
                    class_name_en = a.ClassNameEn,
                    at_risk_count = a.AtRiskCount,
                    total_students = a.TotalStudents,
                }),
            },
            export_url = $"{CreateRoute}/{row.SchoolReportId}/export",
        });
    }

    public static async Task<IResult> HandleExportAsync(
        Guid schoolReportId,
        HttpContext http,
        ISchoolReportRepository reports,
        ISchoolReportBlobStore blobs,
        CancellationToken ct)
    {
        if (!TryResolveAdmin(http, out var tenantId, out var schoolTenantId, out _))
            return Results.Unauthorized();

        var row = await reports.GetByIdAsync(tenantId, schoolTenantId, schoolReportId, ct);
        if (row is null) return Results.NotFound(new { error = "report_not_found" });
        if (row.Status != "ready" || string.IsNullOrWhiteSpace(row.ExportBlobKey))
            return Results.Conflict(new { error = "report_not_ready" });

        var bytes = await blobs.GetAsync(row.ExportBlobKey!, ct);
        if (bytes is null) return Results.NotFound(new { error = "export_blob_missing" });

        var fileName = $"school-report-{row.SchoolReportId:N}.pdf";
        return Results.File(bytes, contentType: "application/pdf", fileDownloadName: fileName);
    }

    public static bool IsValidReportType(string reportType)
        => reportType is "mastery_trends" or "engagement_summary" or "exam_performance" or "at_risk_distribution";

    public static bool TryResolveAdmin(HttpContext http, out Guid tenantId, out Guid schoolTenantId, out Guid adminId)
    {
        tenantId = schoolTenantId = adminId = Guid.Empty;
        return SchoolManagementHeaders.TryGetTenantId(http, out tenantId)
            && SchoolManagementHeaders.TryGetSchoolTenantId(http, out schoolTenantId)
            && SchoolManagementHeaders.TryGetSchoolAdminId(http, out adminId);
    }
}
