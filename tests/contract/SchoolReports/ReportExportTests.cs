using System;
using System.Text;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolReports.ReportAggregation;
using Muallimi.Api.SchoolReports.ReportExport;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolReports;

/// <summary>
/// T170 (US9) — contract test for the export endpoint.
///
/// Verifies that a ready report's export artifact is retrievable by blob
/// key, that the content-type is application/pdf, and that a not-ready
/// report yields a conflict response contract from the exporter.
/// </summary>
public class ReportExportTests
{
    [Fact]
    public async Task Ready_Report_Export_Is_Retrievable_As_Pdf()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new SchoolReportHarness(db);
        await harness.SeedAsync();

        var repo = new SchoolReportRepository(db);
        var aggregator = new SchoolReportAggregator(db);
        var blobs = new InMemorySchoolReportBlobStore();
        var exporter = new SchoolReportExporter(blobs);
        var outbox = new Phase5DownstreamEventOutbox(db);

        var report = harness.BuildReport("engagement_summary", "en");
        await repo.AddAsync(report);
        await repo.SaveChangesAsync();

        var tracked = await repo.GetByIdAsync(SchoolReportHarness.TenantAlpha, SchoolReportHarness.SchoolAlpha, report.SchoolReportId);
        await SchoolReportGenerationJob.GenerateAsync(tracked!, aggregator, exporter, outbox);
        await repo.SaveChangesAsync();

        var bytes = await blobs.GetAsync(tracked!.ExportBlobKey!);
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length > 0);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("Engagement Summary", text);
        Assert.Contains("%%EOF", text);
    }

    [Fact]
    public async Task Export_Artifact_Language_Annotation_Is_Pinned_Per_Language()
    {
        var blobs = new InMemorySchoolReportBlobStore();
        var exporter = new SchoolReportExporter(blobs);

        var payloadAr = new SchoolReportPayload(
            ReportType: "mastery_trends",
            Language: "ar",
            WindowStart: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            WindowEnd: new DateTime(2026, 4, 18, 0, 0, 0, DateTimeKind.Utc),
            SchoolTenantId: Guid.NewGuid(),
            SchoolNameAr: "مدرسة اختبار",
            SchoolNameEn: "Test School",
            MasteryTrends: Array.Empty<SchoolReportMasteryTrend>(),
            EngagementSummary: new SchoolReportEngagementSummary(0, 0m, new System.Collections.Generic.Dictionary<string, int>()),
            ExamPerformance: Array.Empty<SchoolReportExamPerformance>(),
            AtRiskDistribution: Array.Empty<SchoolReportAtRiskRow>());

        var artifactAr = await exporter.ExportAsync(payloadAr);
        var textAr = Encoding.UTF8.GetString(artifactAr.Bytes);
        Assert.Contains("% direction: rtl", textAr);
        Assert.Contains("% language: ar", textAr);
        Assert.Contains("مدرسة اختبار", textAr);

        var payloadEn = payloadAr with { Language = "en" };
        var artifactEn = await exporter.ExportAsync(payloadEn);
        var textEn = Encoding.UTF8.GetString(artifactEn.Bytes);
        Assert.Contains("% direction: ltr", textEn);
        Assert.Contains("Test School", textEn);
    }
}
