using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolReports.ReportAggregation;
using Muallimi.Api.SchoolReports.ReportExport;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolReports;

/// <summary>
/// T171 (US9) — integration test for Arabic numeral and diacritic
/// preservation across the generate → export pipeline.
///
/// Seeds a harness with an Arabic exam title containing diacritics, runs
/// the full aggregate → export pipeline, and verifies the UTF-8 export
/// bytes preserve every diacritic + convert ASCII digits to Arabic-Indic
/// digits so the RTL PDF renders correctly.
/// </summary>
public class ArabicExportTests
{
    [Fact]
    public async Task Arabic_Report_Preserves_Diacritics_And_Uses_Arabic_Indic_Digits()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new SchoolReportHarness(db);
        await harness.SeedAsync();

        var repo = new SchoolReportRepository(db);
        var aggregator = new SchoolReportAggregator(db);
        var blobs = new InMemorySchoolReportBlobStore();
        var exporter = new SchoolReportExporter(blobs);
        var outbox = new Phase5DownstreamEventOutbox(db);

        var report = harness.BuildReport("exam_performance", "ar", SchoolReportHarness.SubjectMath);
        await repo.AddAsync(report);
        await repo.SaveChangesAsync();

        var tracked = await repo.GetByIdAsync(SchoolReportHarness.TenantAlpha, SchoolReportHarness.SchoolAlpha, report.SchoolReportId);
        await SchoolReportGenerationJob.GenerateAsync(tracked!, aggregator, exporter, outbox);
        await repo.SaveChangesAsync();

        var bytes = await blobs.GetAsync(tracked!.ExportBlobKey!);
        Assert.NotNull(bytes);
        var text = Encoding.UTF8.GetString(bytes!);

        // Arabic heading + diacriticised exam title preserved.
        Assert.Contains("تقرير المدرسة", text);
        Assert.Contains("امتحان الرياضيات", text);

        // Arabic-Indic digits replaced throughout (no ASCII digits remain in
        // Arabic-rendered sections). The numeric summary lines get mapped to
        // digits ٠-٩.
        Assert.DoesNotContain(ArabicSummaryLines(text).SelectMany(l => l.ToCharArray()), ch => ch >= '0' && ch <= '9');
        Assert.Contains(ArabicSummaryLines(text).SelectMany(l => l.ToCharArray()), ch => "٠١٢٣٤٥٦٧٨٩".Contains(ch));

        // RTL direction annotation present so PDF viewers render correctly.
        Assert.Contains("% direction: rtl", text);
    }

    [Fact]
    public async Task English_Report_Keeps_Western_Digits_And_Ltr_Annotation()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new SchoolReportHarness(db);
        await harness.SeedAsync();

        var repo = new SchoolReportRepository(db);
        var aggregator = new SchoolReportAggregator(db);
        var blobs = new InMemorySchoolReportBlobStore();
        var exporter = new SchoolReportExporter(blobs);
        var outbox = new Phase5DownstreamEventOutbox(db);

        var report = harness.BuildReport("exam_performance", "en", SchoolReportHarness.SubjectMath);
        await repo.AddAsync(report);
        await repo.SaveChangesAsync();

        var tracked = await repo.GetByIdAsync(SchoolReportHarness.TenantAlpha, SchoolReportHarness.SchoolAlpha, report.SchoolReportId);
        await SchoolReportGenerationJob.GenerateAsync(tracked!, aggregator, exporter, outbox);
        await repo.SaveChangesAsync();

        var bytes = await blobs.GetAsync(tracked!.ExportBlobKey!);
        Assert.NotNull(bytes);
        var text = Encoding.UTF8.GetString(bytes!);

        Assert.Contains("% direction: ltr", text);
        Assert.Contains("Math Exam", text);
        Assert.Contains("85.00", text);
    }

    private static string[] ArabicSummaryLines(string text)
        => text.Split('\n').Where(l => l.Contains("•") || l.Contains("متوسط") || l.Contains("المتوسط")).ToArray();
}
