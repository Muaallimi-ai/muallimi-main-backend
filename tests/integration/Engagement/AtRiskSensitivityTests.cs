using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T139 (US8) — Sensitivity bound on the reference at-risk set.
///
/// Replays a student whose pattern crosses the documented threshold:
/// sustained low mastery + a real Phase 1 deep link. The orchestrator MUST
/// raise a flag, link a guardrail-passed intervention prompt, and emit
/// <c>at_risk_flagged</c> through the outbox in the same unit of work.
/// </summary>
public class AtRiskSensitivityTests
{
    [Fact]
    public async Task Sustained_Low_Mastery_Raises_Flag_With_Linked_Prompt()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var harness = new AtRiskTestHarness();

        await harness.SeedSustainedLowMasteryAsync(
            tenantId, studentId, subjectId, topicId,
            masteryScore: 0.20m, contributingRecords: 8);

        var outcome = await harness.Orchestrator.EvaluateStudentAsync(
            tenantId, studentId, "corr-sensitivity-1");

        Assert.True(outcome.Raised);
        Assert.False(outcome.Cleared);
        Assert.NotNull(outcome.AtRiskFlagId);
        Assert.NotNull(outcome.InterventionPromptId);
        Assert.Equal("pass", outcome.FinalStage);

        var flag = await harness.Db.AtRiskFlags
            .IgnoreQueryFilters()
            .SingleAsync(f => f.AtRiskFlagId == outcome.AtRiskFlagId!.Value);
        Assert.Equal(harness.Catalogue.Current.Version, flag.ThresholdVersion);
        Assert.Null(flag.ClearedAt);
        Assert.Equal(outcome.InterventionPromptId, flag.LinkedInterventionPromptId);

        var prompt = await harness.Db.InterventionPrompts
            .IgnoreQueryFilters()
            .SingleAsync(p => p.InterventionPromptId == outcome.InterventionPromptId!.Value);
        Assert.False(string.IsNullOrWhiteSpace(prompt.BodyAr));
        Assert.False(string.IsNullOrWhiteSpace(prompt.BodyEn));
        Assert.Equal(flag.AtRiskFlagId, prompt.OriginatingFlagId);

        var events = await harness.Db.Phase4DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                        && e.StudentId == studentId
                        && e.EventKind == "at_risk_flagged")
            .ToListAsync();
        var evt = Assert.Single(events);
        Assert.Equal("queued", evt.DeliveryState);
        Assert.Contains(flag.AtRiskFlagId.ToString(), evt.Payload);

        // Tutor runtime was invoked twice (one per language) with the
        // reserved intervention_prompt key — the guardrail chain pass-through.
        Assert.Equal(2, harness.Tutor.Calls.Count);
        Assert.All(harness.Tutor.Calls, c => Assert.Equal("intervention_prompt", c.PromptKey));
    }

    [Fact]
    public async Task Re_Evaluation_With_Active_Flag_Is_Idempotent()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var harness = new AtRiskTestHarness();

        await harness.SeedSustainedLowMasteryAsync(
            tenantId, studentId, subjectId, topicId,
            masteryScore: 0.20m, contributingRecords: 8);

        var first = await harness.Orchestrator.EvaluateStudentAsync(
            tenantId, studentId, "corr-sensitivity-first");
        Assert.True(first.Raised);

        var second = await harness.Orchestrator.EvaluateStudentAsync(
            tenantId, studentId, "corr-sensitivity-second");
        Assert.False(second.Raised);
        Assert.False(second.Cleared);
        Assert.Equal(first.AtRiskFlagId, second.AtRiskFlagId);

        var flagCount = await harness.Db.AtRiskFlags
            .IgnoreQueryFilters()
            .CountAsync(f => f.TenantId == tenantId && f.StudentId == studentId);
        Assert.Equal(1, flagCount);
    }
}
