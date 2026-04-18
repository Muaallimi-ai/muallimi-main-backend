using System;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolReports.ReportAggregation;
using Muallimi.Api.SchoolReports.ReportExport;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolReports;

/// <summary>
/// T169 (US9) — contract test for school-report generation + status.
///
/// Pins route constants, report-type validation, and the full generation
/// pipeline: POST creates a row with status=generating, the generation job
/// aggregates → exports → flips to ready, the outbox gets a
/// report_generated event, and cross-tenant reads stay isolated.
/// </summary>
public class ReportGenerationTests
{
    [Fact]
    public void Route_Constants_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/reports", SchoolReportEndpoints.CreateRoute);
        Assert.Equal("/api/school-admin/reports", SchoolReportEndpoints.ListRoute);
        Assert.Equal("/api/school-admin/reports/{schoolReportId:guid}", SchoolReportEndpoints.StatusRoute);
        Assert.Equal("/api/school-admin/reports/{schoolReportId:guid}/export", SchoolReportEndpoints.ExportRoute);
    }

    [Theory]
    [InlineData("mastery_trends", true)]
    [InlineData("engagement_summary", true)]
    [InlineData("exam_performance", true)]
    [InlineData("at_risk_distribution", true)]
    [InlineData("grade_summary", false)]
    [InlineData("", false)]
    public void ReportType_Validation_Is_Pinned(string reportType, bool expected)
    {
        Assert.Equal(expected, SchoolReportEndpoints.IsValidReportType(reportType));
    }

    [Fact]
    public async Task Generation_Pipeline_Aggregates_And_Transitions_To_Ready()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new SchoolReportHarness(db);
        await harness.SeedAsync();

        var repo = new SchoolReportRepository(db);
        var aggregator = new SchoolReportAggregator(db);
        var blobs = new InMemorySchoolReportBlobStore();
        var exporter = new SchoolReportExporter(blobs);
        var outbox = new Phase5DownstreamEventOutbox(db);

        var report = harness.BuildReport("mastery_trends", "ar", SchoolReportHarness.SubjectMath);
        await repo.AddAsync(report);
        await repo.SaveChangesAsync();

        // Reload tracked so mutations land in the same context.
        var tracked = await repo.GetByIdAsync(SchoolReportHarness.TenantAlpha, SchoolReportHarness.SchoolAlpha, report.SchoolReportId);
        Assert.NotNull(tracked);

        await SchoolReportGenerationJob.GenerateAsync(tracked!, aggregator, exporter, outbox);
        await repo.SaveChangesAsync();

        Assert.Equal("ready", tracked!.Status);
        Assert.False(string.IsNullOrWhiteSpace(tracked.ExportBlobKey));
        Assert.NotNull(tracked.CompletedAt);

        var outboxRows = db.Phase5DownstreamEvents.Local.ToList();
        Assert.Single(outboxRows);
        Assert.Equal(nameof(Phase5DownstreamEventKind.report_generated), outboxRows[0].EventKind);
        Assert.Equal(SchoolReportHarness.TenantAlpha, outboxRows[0].TenantId);
        Assert.Equal(SchoolReportHarness.SchoolAlpha, outboxRows[0].SchoolTenantId);
    }

    [Fact]
    public async Task Aggregator_Respects_Tenant_Scoping_And_Does_Not_Leak_Cross_School()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new SchoolReportHarness(db);
        await harness.SeedAsync();

        var aggregator = new SchoolReportAggregator(db);
        var report = harness.BuildReport("at_risk_distribution", "ar");
        var payload = await aggregator.AggregateAsync(report);

        Assert.All(payload.AtRiskDistribution, row =>
            Assert.DoesNotContain("Beta", row.ClassNameEn));
        Assert.Contains(payload.AtRiskDistribution, row => row.ClassNameEn.Contains("Grade 7A"));
        Assert.Contains(payload.AtRiskDistribution, row => row.AtRiskCount == 1);
    }

    [Fact]
    public async Task Exam_Performance_Rollup_Uses_Scored_Submissions_Only()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new SchoolReportHarness(db);
        await harness.SeedAsync();

        var aggregator = new SchoolReportAggregator(db);
        var report = harness.BuildReport("exam_performance", "ar", SchoolReportHarness.SubjectMath);
        var payload = await aggregator.AggregateAsync(report);

        Assert.Single(payload.ExamPerformance);
        var exam = payload.ExamPerformance[0];
        Assert.Equal("Math Exam", exam.ExamTitleEn);
        Assert.Equal(85m, exam.HighestScore);
        Assert.Equal(54m, exam.LowestScore);
    }
}
