using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.Parents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolDashboard;

/// <summary>
/// T088 (US4) — Operator impersonation audit for school admin dashboard.
///
/// Verifies the <see cref="SchoolOperatorImpersonationAuditor"/> contract
/// used by the dashboard and class-detail endpoints:
///   • every impersonated view writes exactly one audit row;
///   • the row carries the operator actor id (NOT the impersonated
///     admin's user identity id);
///   • the row stores the correlation id and reason text;
///   • omitting a reason throws (endpoint defaults a reason before
///     calling the auditor, so the auditor contract stays strict);
///   • operator acting on their own identity throws (guard against a
///     self-impersonation audit).
/// </summary>
public class OperatorImpersonationTests
{
    [Fact]
    public async Task Auditor_Records_School_Admin_Dashboard_Surface()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        await harness.SeedAsync(includeBeta: false);

        var auditor = harness.BuildAuditor();
        var operatorActorId = Guid.NewGuid();
        var targetUserIdentityId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("D");

        var auditId = await auditor.RecordViewAsync(
            tenantId: DashboardHarness.TenantAlpha,
            operatorActorId: operatorActorId,
            schoolTenantId: DashboardHarness.SchoolAlpha,
            targetUserIdentityId: targetUserIdentityId,
            surface: SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
            reason: "support-ticket-9241",
            correlationId: correlationId,
            ct: CancellationToken.None);

        var row = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .FirstAsync(r => r.OperatorImpersonationAuditId == auditId);

        Assert.Equal(DashboardHarness.TenantAlpha, row.TenantId);
        Assert.Equal(operatorActorId, row.OperatorActorId);
        Assert.NotEqual(targetUserIdentityId, row.OperatorActorId);
        Assert.Equal("school_admin_dashboard", row.Surface);
        Assert.Equal("support-ticket-9241", row.Reason);
        Assert.Equal(correlationId, row.CorrelationId);
        // Phase 5 overloads TargetParentProfileId to carry the school tenant id
        // so the Phase 4 retention pipeline continues to work.
        Assert.Equal(DashboardHarness.SchoolAlpha, row.TargetParentProfileId);
        Assert.Equal(targetUserIdentityId, row.TargetChildId);
    }

    [Fact]
    public async Task Auditor_Rejects_Missing_Reason()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        var auditor = harness.BuildAuditor();

        await Assert.ThrowsAsync<ArgumentException>(() => auditor.RecordViewAsync(
            tenantId: DashboardHarness.TenantAlpha,
            operatorActorId: Guid.NewGuid(),
            schoolTenantId: DashboardHarness.SchoolAlpha,
            targetUserIdentityId: Guid.NewGuid(),
            surface: SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
            reason: " ",
            correlationId: "corr",
            ct: CancellationToken.None));
    }

    [Fact]
    public async Task Auditor_Rejects_Self_Impersonation()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new DashboardHarness(db);
        var auditor = harness.BuildAuditor();
        var sharedId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => auditor.RecordViewAsync(
            tenantId: DashboardHarness.TenantAlpha,
            operatorActorId: sharedId,
            schoolTenantId: DashboardHarness.SchoolAlpha,
            targetUserIdentityId: sharedId,
            surface: SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
            reason: "self-test",
            correlationId: "corr",
            ct: CancellationToken.None));
    }
}
