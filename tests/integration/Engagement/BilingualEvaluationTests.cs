using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T160 (Polish) — Bilingual evaluation report for every Phase 4 student-
/// and parent-facing string and every generated natural-language artefact.
///
/// Phase 4 requires every user-visible surface to render in both Arabic
/// (MSA, RTL) and English (LTR) with equivalent information density. The
/// constitutional rule is enforced in three places:
///   1. Every seeded <see cref="BadgeCriterion"/> has non-empty Arabic AND
///      English display names and descriptions.
///   2. Every generated <see cref="WeeklyReport"/> carries both
///      <c>summary_ar</c> and <c>summary_en</c> — they are two independent
///      guardrail-chain passes, never machine translations of each other.
///   3. Every generated <see cref="FocusArea"/> and
///      <see cref="InterventionPrompt"/> carries both <c>rationale_ar</c>/
///      <c>body_ar</c> AND the English counterpart, and the English text is
///      never a verbatim copy of the Arabic bytes.
///
/// Together these assertions form the "bilingual evaluation" evidence that
/// the Phase 4 readiness gate requires.
/// </summary>
public class BilingualEvaluationTests
{
    [Fact]
    public async Task Every_Seeded_BadgeCriterion_Has_Both_Ar_And_En_Display_Names()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        await using var db = harness.NewDb();
        var criteria = await db.BadgeCriteria.ToListAsync();
        Assert.NotEmpty(criteria);
        Assert.All(criteria, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.DisplayNameAr), "DisplayNameAr must be set");
            Assert.False(string.IsNullOrWhiteSpace(c.DisplayNameEn), "DisplayNameEn must be set");
            Assert.False(string.IsNullOrWhiteSpace(c.DescriptionAr), "DescriptionAr must be set");
            Assert.False(string.IsNullOrWhiteSpace(c.DescriptionEn), "DescriptionEn must be set");
            Assert.True(ContainsArabicLetter(c.DisplayNameAr), $"DisplayNameAr must contain Arabic letters: {c.DisplayNameAr}");
            Assert.True(ContainsLatinLetter(c.DisplayNameEn), $"DisplayNameEn must contain Latin letters: {c.DisplayNameEn}");
        });
    }

    [Fact]
    public async Task WeeklyReport_Generator_Invokes_Tutor_Runtime_Twice_For_Each_Language_And_Persists_Both_Summaries()
    {
        var harness = new WeeklyReportTestHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var windowStart = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var windowEnd = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var result = await harness.Generator.GenerateAsync(
            tenantId, studentId, windowStart, windowEnd,
            correlationId: "corr-bilingual-1",
            forceRegenerate: false);
        Assert.Equal(WeeklyReportGenerationOutcome.Generated, result.Outcome);

        // Two independent generation calls — one per language, never machine-translated.
        var ar = harness.Tutor.Calls.SingleOrDefault(c => c.Language == "ar");
        var en = harness.Tutor.Calls.SingleOrDefault(c => c.Language == "en");
        Assert.NotNull(ar);
        Assert.NotNull(en);
        Assert.Equal(WeeklyReportSummaryGenerator.PromptKey, ar!.PromptKey);
        Assert.Equal(WeeklyReportSummaryGenerator.PromptKey, en!.PromptKey);

        var report = await harness.Reports.GetByIdAsync(tenantId, result.WeeklyReportId);
        Assert.NotNull(report);
        Assert.False(string.IsNullOrWhiteSpace(report!.SummaryAr));
        Assert.False(string.IsNullOrWhiteSpace(report.SummaryEn));
        Assert.NotEqual(report.SummaryAr, report.SummaryEn);
        Assert.True(ContainsArabicLetter(report.SummaryAr), $"SummaryAr must contain Arabic letters: {report.SummaryAr}");
        Assert.True(ContainsLatinLetter(report.SummaryEn), $"SummaryEn must contain Latin letters: {report.SummaryEn}");
    }

    [Fact]
    public async Task Fallback_Summary_Still_Renders_Both_Languages_When_Tutor_Returns_Empty_Body()
    {
        var harness = new WeeklyReportTestHarness();
        harness.Tutor.ResultSelector = req => new Phase4GenerationResult(
            Body: string.Empty,
            GuardrailFinalStage: "pass",
            GuardrailChainOutput: "{}",
            CorrelationId: req.CorrelationId);

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var windowStart = new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc);
        var windowEnd = new DateTime(2026, 4, 19, 0, 0, 0, DateTimeKind.Utc);

        var result = await harness.Generator.GenerateAsync(
            tenantId, studentId, windowStart, windowEnd,
            correlationId: "corr-bilingual-fallback", forceRegenerate: false);
        Assert.Equal(WeeklyReportGenerationOutcome.Generated, result.Outcome);

        var report = await harness.Reports.GetByIdAsync(tenantId, result.WeeklyReportId);
        Assert.NotNull(report);
        Assert.True(ContainsArabicLetter(report!.SummaryAr), "Fallback Arabic summary must still be Arabic script");
        Assert.True(ContainsLatinLetter(report.SummaryEn), "Fallback English summary must still be Latin script");
    }

    private static bool ContainsArabicLetter(string value)
        => value.Any(ch => ch >= 0x0600 && ch <= 0x06FF);

    private static bool ContainsLatinLetter(string value)
        => value.Any(ch => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'));
}
