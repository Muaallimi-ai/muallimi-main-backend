using System;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Tests.Integration.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T065 (US2) — Operator impersonation audit coverage.
///
/// Asserts that every impersonated dashboard/selector render writes an
/// <c>OperatorImpersonationAudit</c> row, that the operator identity can
/// never equal the parent identity, and that the reason field is
/// required (missing reason is a readiness-gate failure, not a silent
/// pass-through).
/// </summary>
public class OperatorImpersonationAuditTests
{
    [Fact]
    public async Task Every_Impersonated_View_Writes_An_Audit_Row()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var auditor = new OperatorImpersonationAuditor(db);

        var operatorActorId = Guid.NewGuid();
        var correlationA = Guid.NewGuid().ToString("D");
        var correlationB = Guid.NewGuid().ToString("D");

        await auditor.RecordViewAsync(
            tenantId: TenantIsolationHarness.TenantAlpha,
            operatorActorId: operatorActorId,
            targetParentProfileId: TenantIsolationHarness.SharedParentIdAlpha,
            targetChildId: TenantIsolationHarness.SharedStudentIdAlpha,
            surface: OperatorImpersonationSurfaces.ParentDashboard,
            reason: "support_case_1001",
            correlationId: correlationA);

        await auditor.RecordViewAsync(
            tenantId: TenantIsolationHarness.TenantAlpha,
            operatorActorId: operatorActorId,
            targetParentProfileId: TenantIsolationHarness.SharedParentIdAlpha,
            targetChildId: null,
            surface: OperatorImpersonationSurfaces.ParentDashboard,
            reason: "support_case_1001_child_selector",
            correlationId: correlationB);

        var rows = db.OperatorImpersonationAudits.OrderBy(a => a.ViewedAt).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(TenantIsolationHarness.TenantAlpha, r.TenantId);
            Assert.Equal(operatorActorId, r.OperatorActorId);
            Assert.Equal(TenantIsolationHarness.SharedParentIdAlpha, r.TargetParentProfileId);
            Assert.Equal(OperatorImpersonationSurfaces.ParentDashboard, r.Surface);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
            Assert.False(string.IsNullOrWhiteSpace(r.CorrelationId));
        });
        Assert.Contains(rows, r => r.TargetChildId == TenantIsolationHarness.SharedStudentIdAlpha);
        Assert.Contains(rows, r => r.TargetChildId == null);
    }

    [Fact]
    public async Task Operator_Actor_Id_Must_Not_Equal_Parent_Id()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var auditor = new OperatorImpersonationAuditor(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            auditor.RecordViewAsync(
                tenantId: TenantIsolationHarness.TenantAlpha,
                operatorActorId: TenantIsolationHarness.SharedParentIdAlpha,
                targetParentProfileId: TenantIsolationHarness.SharedParentIdAlpha,
                targetChildId: TenantIsolationHarness.SharedStudentIdAlpha,
                surface: OperatorImpersonationSurfaces.ParentDashboard,
                reason: "attempt_self_view",
                correlationId: Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public async Task Missing_Reason_Is_Rejected_As_ReadinessGate_Failure()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var auditor = new OperatorImpersonationAuditor(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            auditor.RecordViewAsync(
                tenantId: TenantIsolationHarness.TenantAlpha,
                operatorActorId: Guid.NewGuid(),
                targetParentProfileId: TenantIsolationHarness.SharedParentIdAlpha,
                targetChildId: TenantIsolationHarness.SharedStudentIdAlpha,
                surface: OperatorImpersonationSurfaces.ParentDashboard,
                reason: "   ",
                correlationId: Guid.NewGuid().ToString("D")));
    }
}
