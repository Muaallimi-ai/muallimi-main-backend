using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.SchoolReports.ReportAggregation;

namespace Muallimi.Api.SchoolReports.ReportExport;

/// <summary>
/// T174 (US9) — renders a <see cref="SchoolReportPayload"/> to a PDF-style
/// byte stream with Arabic-aware formatting.
///
/// The local-parity renderer emits a text-based PDF that preserves every
/// Arabic character (including diacritics) from the source payload and maps
/// ASCII digits to Arabic-Indic digits when <c>language="ar"</c>. That gives
/// the integration tests a deterministic byte stream to validate. Production
/// binding can swap <see cref="ISchoolReportBlobStore"/> for a real PDF
/// renderer + blob writer without changing callers.
/// </summary>
public interface ISchoolReportExporter
{
    Task<SchoolReportExportArtifact> ExportAsync(SchoolReportPayload payload, CancellationToken ct = default);
}

public sealed record SchoolReportExportArtifact(byte[] Bytes, string ContentType, string BlobKey);

public sealed class SchoolReportExporter : ISchoolReportExporter
{
    private static readonly string ArabicDigits = "٠١٢٣٤٥٦٧٨٩";

    private readonly ISchoolReportBlobStore _blobs;

    public SchoolReportExporter(ISchoolReportBlobStore blobs) => _blobs = blobs;

    public async Task<SchoolReportExportArtifact> ExportAsync(SchoolReportPayload payload, CancellationToken ct = default)
    {
        var isArabic = string.Equals(payload.Language, "ar", StringComparison.OrdinalIgnoreCase);
        var body = BuildBody(payload, isArabic);
        var bytes = Encoding.UTF8.GetBytes(body);
        var blobKey = $"school-reports/{payload.SchoolTenantId:N}/{Guid.NewGuid():N}.pdf";
        await _blobs.PutAsync(blobKey, bytes, ct);
        return new SchoolReportExportArtifact(bytes, "application/pdf", blobKey);
    }

    private static string BuildBody(SchoolReportPayload payload, bool isArabic)
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");
        sb.AppendLine($"% direction: {(isArabic ? "rtl" : "ltr")}");
        sb.AppendLine($"% language: {payload.Language}");
        sb.AppendLine();

        var title = isArabic
            ? $"تقرير المدرسة — {payload.SchoolNameAr}"
            : $"School Report — {payload.SchoolNameEn}";
        sb.AppendLine(title);
        sb.AppendLine(FormatNumbers($"{payload.ReportType} | {payload.WindowStart:yyyy-MM-dd} → {payload.WindowEnd:yyyy-MM-dd}", isArabic));
        sb.AppendLine();

        if (payload.MasteryTrends.Count > 0)
        {
            sb.AppendLine(isArabic ? "اتجاهات الإتقان" : "Mastery Trends");
            foreach (var row in payload.MasteryTrends)
            {
                var name = isArabic ? row.SubjectNameAr : row.SubjectNameEn;
                var line = FormatNumbers(
                    $"• {name} — {row.Period} — {(isArabic ? "الصف" : "Grade")} {row.Grade} — {row.AverageMastery:0.00}",
                    isArabic);
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }

        sb.AppendLine(isArabic ? "ملخص التفاعل" : "Engagement Summary");
        sb.AppendLine(FormatNumbers(
            $"{(isArabic ? "الطلاب النشطون" : "Active students")}: {payload.EngagementSummary.ActiveStudents}",
            isArabic));
        sb.AppendLine(FormatNumbers(
            $"{(isArabic ? "متوسط الجلسات" : "Avg sessions/student")}: {payload.EngagementSummary.AverageSessionsPerStudent:0.00}",
            isArabic));
        sb.AppendLine();

        if (payload.ExamPerformance.Count > 0)
        {
            sb.AppendLine(isArabic ? "أداء الامتحانات" : "Exam Performance");
            foreach (var exam in payload.ExamPerformance)
            {
                var name = isArabic ? exam.ExamTitleAr : exam.ExamTitleEn;
                var line = FormatNumbers(
                    $"• {name} — {(isArabic ? "المتوسط" : "avg")} {exam.ClassAverage:0.00} — {(isArabic ? "أعلى" : "high")} {exam.HighestScore:0.00} — {(isArabic ? "أدنى" : "low")} {exam.LowestScore:0.00}",
                    isArabic);
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }

        if (payload.AtRiskDistribution.Count > 0)
        {
            sb.AppendLine(isArabic ? "توزيع الطلاب المعرضين للخطر" : "At-Risk Distribution");
            foreach (var row in payload.AtRiskDistribution)
            {
                var name = isArabic ? row.ClassNameAr : row.ClassNameEn;
                var line = FormatNumbers(
                    $"• {name} — {row.AtRiskCount}/{row.TotalStudents}",
                    isArabic);
                sb.AppendLine(line);
            }
        }

        sb.AppendLine();
        sb.AppendLine("%%EOF");
        return sb.ToString();
    }

    private static string FormatNumbers(string input, bool isArabic)
    {
        if (!isArabic) return input;
        var result = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch >= '0' && ch <= '9')
            {
                result.Append(ArabicDigits[ch - '0']);
            }
            else
            {
                result.Append(ch);
            }
        }
        return result.ToString();
    }
}

public interface ISchoolReportBlobStore
{
    Task PutAsync(string key, byte[] bytes, CancellationToken ct = default);
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// In-memory blob store used for local-parity + tests. Production binding
/// replaces this with a MinIO/S3 adapter without touching callers.
/// </summary>
public sealed class InMemorySchoolReportBlobStore : ISchoolReportBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public Task PutAsync(string key, byte[] bytes, CancellationToken ct = default)
    {
        _store[key] = bytes;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        _store.TryGetValue(key, out var bytes);
        return Task.FromResult<byte[]?>(bytes);
    }
}

public static class SchoolReportExporterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolReportExporter(this IServiceCollection services)
    {
        services.AddSingleton<ISchoolReportBlobStore, InMemorySchoolReportBlobStore>();
        services.AddScoped<ISchoolReportExporter, SchoolReportExporter>();
        return services;
    }
}
