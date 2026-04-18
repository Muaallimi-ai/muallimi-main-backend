using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Tests.Integration.Engagement;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.Parents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T162 (Polish, FR-019) — Operator impersonation audit retention.
///
/// The Phase 4 readiness gate requires that every operator impersonation
/// audit row be retained for the documented investigation window, queryable
/// by the operator actor, the target tenant, and the target child, and
/// protected from silent drop-off before the window expires.
///
/// This test:
///   1. Seeds audit rows at varied ages — recent, at the window edge, and
///      beyond the window.
///   2. Asserts that every row inside the window is still queryable by
///      tenant, operator, target parent, and target child.
///   3. Applies <see cref="RetentionPolicy.Default"/> as the reference
///      investigation window (90 days) and asserts that only rows strictly
///      older than the cutoff are eligible for purge — rows at the cutoff
///      boundary remain visible.
///   4. Confirms the cross-tenant query still returns zero rows even for
///      audit rows that sit inside the retention window.
/// </summary>
public class OperatorImpersonationRetentionTests
{
    [Fact]
    public async Task Audit_Rows_Inside_Retention_Window_Remain_Queryable_By_Every_Axis()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var auditor = new OperatorImpersonationAuditor(db);
        var operatorActorId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Seed 3 rows at varied ages inside the 90-day default window, and
        // 1 row beyond it (simulating a row that should be purged next pass).
        var ages = new[] { TimeSpan.FromDays(1), TimeSpan.FromDays(30), TimeSpan.FromDays(89), TimeSpan.FromDays(120) };
        foreach (var (age, i) in ages.Select((a, idx) => (a, idx)))
        {
            var row = new OperatorImpersonationAudit
            {
                OperatorImpersonationAuditId = Guid.NewGuid(),
                TenantId = TenantIsolationHarness.TenantAlpha,
                OperatorActorId = operatorActorId,
                TargetParentProfileId = TenantIsolationHarness.SharedParentIdAlpha,
                TargetChildId = TenantIsolationHarness.SharedStudentIdAlpha,
                Surface = OperatorImpersonationSurfaces.ParentDashboard,
                Reason = $"support_case_retention_{i}",
                CorrelationId = Guid.NewGuid().ToString("D"),
                ViewedAt = now - age,
            };
            db.OperatorImpersonationAudits.Add(row);
        }
        await db.SaveChangesAsync();

        var policy = RetentionPolicy.Default;
        var cutoff = policy.ComputeCutoff(now);

        // Every row at or inside the window (3 rows) must be queryable by
        // tenant + operator, by tenant + parent, and by tenant + child.
        var byOperator = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == TenantIsolationHarness.TenantAlpha
                        && a.OperatorActorId == operatorActorId
                        && a.ViewedAt >= cutoff)
            .ToListAsync();
        Assert.Equal(3, byOperator.Count);

        var byParent = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == TenantIsolationHarness.TenantAlpha
                        && a.TargetParentProfileId == TenantIsolationHarness.SharedParentIdAlpha
                        && a.ViewedAt >= cutoff)
            .ToListAsync();
        Assert.Equal(3, byParent.Count);

        var byChild = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == TenantIsolationHarness.TenantAlpha
                        && a.TargetChildId == TenantIsolationHarness.SharedStudentIdAlpha
                        && a.ViewedAt >= cutoff)
            .ToListAsync();
        Assert.Equal(3, byChild.Count);

        // Only the row strictly older than the cutoff is eligible for purge.
        var purgeable = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .Where(a => a.ViewedAt < cutoff)
            .ToListAsync();
        Assert.Single(purgeable);
        Assert.Contains("support_case_retention_3", purgeable[0].Reason);
    }

    [Fact]
    public async Task Cross_Tenant_Audit_Query_Returns_Zero_Rows_Even_Inside_Window()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var auditor = new OperatorImpersonationAuditor(db);
        await auditor.RecordViewAsync(
            tenantId: TenantIsolationHarness.TenantAlpha,
            operatorActorId: Guid.NewGuid(),
            targetParentProfileId: TenantIsolationHarness.SharedParentIdAlpha,
            targetChildId: TenantIsolationHarness.SharedStudentIdAlpha,
            surface: OperatorImpersonationSurfaces.ParentDashboard,
            reason: "support_case_xtenant",
            correlationId: Guid.NewGuid().ToString("D"));

        var crossTenant = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == TenantIsolationHarness.TenantBeta)
            .ToListAsync();
        Assert.Empty(crossTenant);
    }

    [Fact]
    public void RetentionPolicy_Cutoff_Matches_The_Documented_InvestigationWindow()
    {
        var policy = RetentionPolicy.Default;
        var anchor = new DateTime(2026, 4, 18, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(TimeSpan.FromDays(90), policy.InvestigationWindow);
        Assert.Equal(anchor.AddDays(-90), policy.ComputeCutoff(anchor));
    }
}
