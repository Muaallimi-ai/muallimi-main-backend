using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T138 (US8) — False-positive bound on the reference non-at-risk set.
///
/// Replays a student whose pattern stays inside every documented threshold
/// (mastery above the ceiling, no repeated refusals, healthy mock pass
/// ratio, no engagement decline). The orchestrator MUST NOT raise a flag,
/// MUST NOT generate an intervention prompt, and MUST NOT emit a
/// downstream event. This guarantees parents do not receive false-alarm
/// nudges.
/// </summary>
public class AtRiskFalsePositiveTests
{
    [Fact]
    public async Task Healthy_Student_Does_Not_Trip_The_Threshold_Set()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var harness = new AtRiskTestHarness();

        // Confident-band mastery on multiple topics, with a passing mock test.
        await harness.SeedConfidentTopicAsync(tenantId, studentId, masteryScore: 0.82m);
        await harness.SeedConfidentTopicAsync(tenantId, studentId, masteryScore: 0.78m);
        await harness.SeedPassingMockTestAsync(tenantId, studentId, Guid.NewGuid(), Guid.NewGuid());

        var outcome = await harness.Orchestrator.EvaluateStudentAsync(
            tenantId, studentId, "corr-no-fp");

        Assert.False(outcome.Raised);
        Assert.False(outcome.Cleared);
        Assert.Null(outcome.AtRiskFlagId);
        Assert.Null(outcome.InterventionPromptId);

        var flags = await harness.Db.AtRiskFlags
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.StudentId == studentId)
            .ToListAsync();
        Assert.Empty(flags);

        var prompts = await harness.Db.InterventionPrompts
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.StudentId == studentId)
            .ToListAsync();
        Assert.Empty(prompts);

        var events = await harness.Db.Phase4DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.StudentId == studentId
                        && (e.EventKind == "at_risk_flagged" || e.EventKind == "at_risk_cleared"))
            .ToListAsync();
        Assert.Empty(events);
    }

    [Fact]
    public async Task Student_With_No_Activity_Yields_Noop_Not_Raise()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var harness = new AtRiskTestHarness();

        var outcome = await harness.Orchestrator.EvaluateStudentAsync(
            tenantId, studentId, "corr-no-data");

        Assert.False(outcome.Raised);
        Assert.False(outcome.Cleared);
        Assert.Equal("noop", outcome.FinalStage);
    }
}
