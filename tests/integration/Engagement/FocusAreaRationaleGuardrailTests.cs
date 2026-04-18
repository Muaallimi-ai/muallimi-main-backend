using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.FocusAreas;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T104 (US5) — Phase 2 guardrail pass-through on focus-area rationales.
///
/// Each written <see cref="FocusArea"/> row carries a non-empty
/// <c>guardrail_decision_trail_id</c>. The row references a
/// <see cref="GuardrailDecisionTrail"/> whose artefact kind is
/// <c>focus_area_rationale</c> and whose prompt key is
/// <c>focus_area_rationale</c>. Arabic + English passes run as two
/// independent calls so no machine translation sneaks in.
///
/// A <c>refuse</c> stage from the chain blocks the candidate entirely —
/// students and parents never see an un-approved rationale.
/// </summary>
public class FocusAreaRationaleGuardrailTests
{
    [Fact]
    public async Task Written_Focus_Area_Stores_Guardrail_Decision_Trail()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();
        await harness.SeedQuizErrorAsync(tenantId, studentId, subjectId, chapterId, topicId);

        await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-guard-1");

        var rows = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        var row = Assert.Single(rows);
        Assert.NotEqual(Guid.Empty, row.GuardrailDecisionTrailId);
        Assert.False(string.IsNullOrWhiteSpace(row.RationaleAr));
        Assert.False(string.IsNullOrWhiteSpace(row.RationaleEn));

        var trail = await harness.Db.GuardrailDecisionTrails
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.GuardrailDecisionTrailId == row.GuardrailDecisionTrailId);
        Assert.NotNull(trail);
        Assert.Equal(GuardrailDecisionTrailArtefactKinds.FocusAreaRationale, trail!.ArtefactKind);
        Assert.Equal(FocusAreaRationaleGenerator.PromptKey, trail.PromptKey);
        Assert.Equal(tenantId, trail.TenantId);
        Assert.Equal(row.FocusAreaId, trail.ArtefactId);
        Assert.Equal("pass", trail.FinalStage);

        Assert.Equal(2, harness.Tutor.Calls.Count);
        Assert.Contains(harness.Tutor.Calls,
            c => c.Language == "ar" && c.PromptKey == FocusAreaRationaleGenerator.PromptKey);
        Assert.Contains(harness.Tutor.Calls,
            c => c.Language == "en" && c.PromptKey == FocusAreaRationaleGenerator.PromptKey);
    }

    [Fact]
    public async Task Refuse_Stage_Blocks_The_Focus_Area()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var harness = new FocusAreaTestHarness();
        harness.Tutor.ResultSelector = req => new Phase4GenerationResult(
            Body: string.Empty,
            GuardrailFinalStage: "refuse",
            GuardrailChainOutput: "{\"stages\":[{\"name\":\"grounding\",\"verdict\":\"refuse\"}]}",
            CorrelationId: req.CorrelationId);

        await harness.SeedQuizErrorAsync(tenantId, studentId, subjectId, chapterId, topicId);

        var result = await harness.Calculator.RecomputeAsync(tenantId, studentId, "corr-guard-2");

        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.WrittenCount);
        Assert.Equal(1, result.RejectedByGuardrail);

        var rows = await harness.Repository.ListActiveAsync(tenantId, studentId, DateTime.UtcNow);
        Assert.Empty(rows);
    }
}
