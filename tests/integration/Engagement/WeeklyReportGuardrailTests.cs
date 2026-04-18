using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T084 (US3) — Integration test for Phase 2 guardrail chain pass-through.
///
/// Every generated <c>summary_ar</c> and <c>summary_en</c> MUST reference
/// a stored <see cref="GuardrailDecisionTrail"/> row. The tutor runtime
/// wrapper is invoked twice — once per language — with the reserved
/// <c>weekly_report_summary</c> prompt key.
/// </summary>
public class WeeklyReportGuardrailTests
{
    [Fact]
    public async Task Generation_Persists_A_Guardrail_Decision_Trail_Row()
    {
        var harness = new WeeklyReportTestHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var windowStart = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var windowEnd = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var result = await harness.Generator.GenerateAsync(
            tenantId, studentId, windowStart, windowEnd,
            correlationId: "corr-guardrail-1",
            forceRegenerate: false);

        Assert.Equal(WeeklyReportGenerationOutcome.Generated, result.Outcome);

        var report = await harness.Reports.GetByIdAsync(tenantId, result.WeeklyReportId);
        Assert.NotNull(report);
        Assert.Equal("ready", report!.Status);
        Assert.NotEqual(Guid.Empty, report.GuardrailDecisionTrailId);
        Assert.False(string.IsNullOrWhiteSpace(report.SummaryAr));
        Assert.False(string.IsNullOrWhiteSpace(report.SummaryEn));

        var trail = await harness.Db.GuardrailDecisionTrails
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.GuardrailDecisionTrailId == report.GuardrailDecisionTrailId);
        Assert.NotNull(trail);
        Assert.Equal(GuardrailDecisionTrailArtefactKinds.WeeklyReportSummary, trail!.ArtefactKind);
        Assert.Equal(WeeklyReportSummaryGenerator.PromptKey, trail.PromptKey);
        Assert.Equal(tenantId, trail.TenantId);
        Assert.Equal(report.WeeklyReportId, trail.ArtefactId);
        Assert.Equal("pass", trail.FinalStage);

        Assert.Equal(2, harness.Tutor.Calls.Count);
        Assert.Contains(harness.Tutor.Calls, c => c.Language == "ar" && c.PromptKey == WeeklyReportSummaryGenerator.PromptKey);
        Assert.Contains(harness.Tutor.Calls, c => c.Language == "en" && c.PromptKey == WeeklyReportSummaryGenerator.PromptKey);
    }

    [Fact]
    public async Task Refuse_Stage_From_Guardrail_Chain_Marks_Report_Failed()
    {
        var harness = new WeeklyReportTestHarness();
        harness.Tutor.ResultSelector = req => new Phase4GenerationResult(
            Body: string.Empty,
            GuardrailFinalStage: "refuse",
            GuardrailChainOutput: "{\"stages\":[{\"name\":\"grounding\",\"verdict\":\"refuse\"}]}",
            CorrelationId: req.CorrelationId);

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 6, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 12, 0, 0, 0, DateTimeKind.Utc);

        var result = await harness.Generator.GenerateAsync(
            tenantId, studentId, start, end,
            correlationId: "corr-refuse",
            forceRegenerate: false);

        Assert.Equal(WeeklyReportGenerationOutcome.Failed, result.Outcome);
        var report = await harness.Reports.GetByIdAsync(tenantId, result.WeeklyReportId);
        Assert.Equal("failed", report!.Status);
        Assert.NotEqual(Guid.Empty, report.GuardrailDecisionTrailId);
    }
}
