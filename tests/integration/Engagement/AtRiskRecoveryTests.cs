using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T140 (US8) — Recovery clearing.
///
/// When a flagged student returns to healthy patterns the next evaluation
/// MUST clear the flag, set <c>ClearedAt</c>, and emit
/// <c>at_risk_cleared</c>. Manual clearing is not exposed to parents — the
/// recovery is computed.
/// </summary>
public class AtRiskRecoveryTests
{
    [Fact]
    public async Task Recovered_Student_Has_Flag_Cleared_And_Event_Emitted()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var harness = new AtRiskTestHarness();

        await harness.SeedSustainedLowMasteryAsync(
            tenantId, studentId, subjectId, topicId,
            masteryScore: 0.20m, contributingRecords: 8);

        var raised = await harness.Orchestrator.EvaluateStudentAsync(
            tenantId, studentId, "corr-recovery-raise");
        Assert.True(raised.Raised);

        // Recovery: mastery climbs above the recovery floor + a passing mock
        // test lands so the recovery predicate fires.
        await harness.SeedRecoveryMasteryAsync(
            tenantId, studentId, subjectId, topicId, masteryScore: 0.78m);
        await harness.SeedPassingMockTestAsync(tenantId, studentId, subjectId, topicId);

        var cleared = await harness.Orchestrator.EvaluateStudentAsync(
            tenantId, studentId, "corr-recovery-clear");

        Assert.True(cleared.Cleared);
        Assert.Equal(raised.AtRiskFlagId, cleared.AtRiskFlagId);

        var flag = await harness.Db.AtRiskFlags
            .IgnoreQueryFilters()
            .SingleAsync(f => f.AtRiskFlagId == raised.AtRiskFlagId!.Value);
        Assert.NotNull(flag.ClearedAt);

        var clearedEvents = await harness.Db.Phase4DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                        && e.StudentId == studentId
                        && e.EventKind == "at_risk_cleared")
            .ToListAsync();
        var evt = Assert.Single(clearedEvents);
        Assert.Contains(flag.AtRiskFlagId.ToString(), evt.Payload);

        // Active query reflects no live flag.
        var active = await harness.Flags.GetActiveAsync(tenantId, studentId);
        Assert.Null(active);
    }
}
