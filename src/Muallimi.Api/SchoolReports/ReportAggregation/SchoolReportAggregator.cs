using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolReports.ReportAggregation;

/// <summary>
/// T173 (US9) — aggregates the payload that the report view + export renders.
///
/// Snapshot is computed from <see cref="SchoolAggregateView"/> (per-class and
/// per-(grade, subject) rows) plus live Phase 5 exam submissions and Phase 4
/// AtRiskFlag rows scoped to the school. Cross-school rows are excluded by
/// construction — every query joins on <c>school_tenant_id</c>.
///
/// Four report types supported: <c>mastery_trends</c>,
/// <c>engagement_summary</c>, <c>exam_performance</c>,
/// <c>at_risk_distribution</c>. The four rollups populate a single
/// <see cref="SchoolReportPayload"/> so export + view render off the same
/// structure.
/// </summary>
public sealed record SchoolReportMasteryTrend(
    string Period,
    int Grade,
    string SubjectNameAr,
    string SubjectNameEn,
    decimal AverageMastery);

public sealed record SchoolReportEngagementSummary(
    int ActiveStudents,
    decimal AverageSessionsPerStudent,
    IReadOnlyDictionary<string, int> StreakDistribution);

public sealed record SchoolReportExamPerformance(
    string ExamTitleAr,
    string ExamTitleEn,
    decimal ClassAverage,
    decimal HighestScore,
    decimal LowestScore);

public sealed record SchoolReportAtRiskRow(
    string ClassNameAr,
    string ClassNameEn,
    int AtRiskCount,
    int TotalStudents);

public sealed record SchoolReportPayload(
    string ReportType,
    string Language,
    DateTime WindowStart,
    DateTime WindowEnd,
    Guid SchoolTenantId,
    string SchoolNameAr,
    string SchoolNameEn,
    IReadOnlyList<SchoolReportMasteryTrend> MasteryTrends,
    SchoolReportEngagementSummary EngagementSummary,
    IReadOnlyList<SchoolReportExamPerformance> ExamPerformance,
    IReadOnlyList<SchoolReportAtRiskRow> AtRiskDistribution);

public interface ISchoolReportAggregator
{
    Task<SchoolReportPayload> AggregateAsync(SchoolReport report, CancellationToken ct = default);
}

public sealed class SchoolReportAggregator : ISchoolReportAggregator
{
    private readonly MuallimiDbContext _db;

    public SchoolReportAggregator(MuallimiDbContext db) => _db = db;

    public async Task<SchoolReportPayload> AggregateAsync(SchoolReport report, CancellationToken ct = default)
    {
        var school = await _db.SchoolTenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(s => s.TenantId == report.TenantId && s.SchoolTenantId == report.SchoolTenantId, ct);

        var mastery = await BuildMasteryTrendsAsync(report, ct);
        var engagement = await BuildEngagementAsync(report, ct);
        var exams = await BuildExamPerformanceAsync(report, ct);
        var atRisk = await BuildAtRiskAsync(report, ct);

        return new SchoolReportPayload(
            ReportType: report.ReportType,
            Language: report.Language,
            WindowStart: report.WindowStart,
            WindowEnd: report.WindowEnd,
            SchoolTenantId: report.SchoolTenantId,
            SchoolNameAr: school.SchoolNameAr,
            SchoolNameEn: school.SchoolNameEn,
            MasteryTrends: mastery,
            EngagementSummary: engagement,
            ExamPerformance: exams,
            AtRiskDistribution: atRisk);
    }

    private async Task<IReadOnlyList<SchoolReportMasteryTrend>> BuildMasteryTrendsAsync(SchoolReport report, CancellationToken ct)
    {
        var rows = await _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == report.TenantId
                && a.SchoolTenantId == report.SchoolTenantId
                && a.ScopeType == "school"
                && a.SubjectId != null
                && a.Grade != null)
            .ToListAsync(ct);

        if (report.GradeFilter is int grade)
        {
            rows = rows.Where(a => a.Grade == grade).ToList();
        }
        if (report.SubjectFilter is Guid subjectFilter)
        {
            rows = rows.Where(a => a.SubjectId == subjectFilter).ToList();
        }

        var period = $"{report.WindowStart:yyyy-MM-dd}_{report.WindowEnd:yyyy-MM-dd}";
        return rows
            .OrderBy(a => a.Grade)
            .ThenBy(a => a.SubjectId)
            .Select(a => new SchoolReportMasteryTrend(
                Period: period,
                Grade: a.Grade ?? 0,
                SubjectNameAr: $"مادة {a.SubjectId}",
                SubjectNameEn: $"Subject {a.SubjectId}",
                AverageMastery: a.AverageMastery))
            .ToList();
    }

    private async Task<SchoolReportEngagementSummary> BuildEngagementAsync(SchoolReport report, CancellationToken ct)
    {
        var classAggregates = await _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == report.TenantId
                && a.SchoolTenantId == report.SchoolTenantId
                && a.ScopeType == "class")
            .ToListAsync(ct);

        var totalStudents = classAggregates.Sum(a => a.ActiveStudentCount);
        var totalStreaks = classAggregates.Sum(a => a.ActiveStreakCount);
        var avgSessions = totalStudents == 0
            ? 0m
            : Math.Round((decimal)totalStreaks / Math.Max(1, totalStudents), 4);

        var streakDistribution = new Dictionary<string, int>
        {
            ["none"] = Math.Max(0, totalStudents - totalStreaks),
            ["active"] = totalStreaks,
        };

        return new SchoolReportEngagementSummary(
            ActiveStudents: totalStudents,
            AverageSessionsPerStudent: avgSessions,
            StreakDistribution: streakDistribution);
    }

    private async Task<IReadOnlyList<SchoolReportExamPerformance>> BuildExamPerformanceAsync(SchoolReport report, CancellationToken ct)
    {
        var examsQuery = _db.Exams
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.TenantId == report.TenantId
                && e.SchoolTenantId == report.SchoolTenantId
                && e.Status == "graded"
                && e.CreatedAt >= report.WindowStart
                && e.CreatedAt <= report.WindowEnd);
        if (report.GradeFilter is int grade)
        {
            examsQuery = examsQuery.Where(e => e.Grade == grade);
        }
        if (report.SubjectFilter is Guid subject)
        {
            examsQuery = examsQuery.Where(e => e.SubjectId == subject);
        }
        var exams = await examsQuery.ToListAsync(ct);
        if (exams.Count == 0) return Array.Empty<SchoolReportExamPerformance>();

        var examIds = exams.Select(e => e.ExamId).ToList();
        var submissions = await _db.ExamSubmissions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.TenantId == report.TenantId
                && examIds.Contains(s.ExamId)
                && s.Score != null)
            .ToListAsync(ct);

        var grouped = submissions.GroupBy(s => s.ExamId).ToDictionary(g => g.Key, g => g.ToList());
        return exams
            .OrderByDescending(e => e.CreatedAt)
            .Select(e =>
            {
                if (!grouped.TryGetValue(e.ExamId, out var subs) || subs.Count == 0)
                {
                    return new SchoolReportExamPerformance(e.TitleAr, e.TitleEn, 0m, 0m, 0m);
                }
                var scores = subs.Where(s => s.Score.HasValue).Select(s => s.Score!.Value).ToList();
                return new SchoolReportExamPerformance(
                    ExamTitleAr: e.TitleAr,
                    ExamTitleEn: e.TitleEn,
                    ClassAverage: Math.Round(scores.Average(), 2),
                    HighestScore: scores.Max(),
                    LowestScore: scores.Min());
            })
            .ToList();
    }

    private async Task<IReadOnlyList<SchoolReportAtRiskRow>> BuildAtRiskAsync(SchoolReport report, CancellationToken ct)
    {
        var classes = await _db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.TenantId == report.TenantId && c.SchoolTenantId == report.SchoolTenantId)
            .ToListAsync(ct);

        var filtered = classes.AsEnumerable();
        if (report.GradeFilter is int grade) filtered = filtered.Where(c => c.Grade == grade);
        if (report.ClassFilter is Guid classFilter) filtered = filtered.Where(c => c.ClassGroupId == classFilter);
        var classList = filtered.ToList();
        if (classList.Count == 0) return Array.Empty<SchoolReportAtRiskRow>();

        var classIds = classList.Select(c => c.ClassGroupId).ToList();
        var aggregates = await _db.SchoolAggregateViews
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.TenantId == report.TenantId
                && a.SchoolTenantId == report.SchoolTenantId
                && a.ScopeType == "class"
                && classIds.Contains(a.ScopeId))
            .ToListAsync(ct);
        var byClass = aggregates.ToDictionary(a => a.ScopeId);

        return classList
            .Select(c =>
            {
                var agg = byClass.TryGetValue(c.ClassGroupId, out var a) ? a : null;
                return new SchoolReportAtRiskRow(
                    ClassNameAr: c.DisplayNameAr,
                    ClassNameEn: c.DisplayNameEn,
                    AtRiskCount: agg?.AtRiskCount ?? 0,
                    TotalStudents: agg?.ActiveStudentCount ?? 0);
            })
            .OrderByDescending(r => r.AtRiskCount)
            .ToList();
    }
}

public static class SchoolReportAggregatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolReportAggregator(this IServiceCollection services)
    {
        services.AddScoped<ISchoolReportAggregator, SchoolReportAggregator>();
        return services;
    }
}
