using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement;

/// <summary>
/// T207 (Polish) — Operator impersonation audit coverage.
///
/// Every impersonated school-admin or teacher view MUST write a single
/// <c>OperatorImpersonationAudit</c> row carrying:
///   • <c>OperatorActorId</c> = the operator's identity (never the
///     impersonated subject).
///   • <c>Surface</c> from
///     <see cref="SchoolOperatorImpersonationSurfaces"/>.
///   • A non-empty <c>Reason</c> (justification).
///   • A <c>CorrelationId</c> matching the triggering request.
///
/// Missing audit rows on a sampled impersonation run are a readiness-gate
/// failure (FR-021 and CR-001). These tests are the last net that catches
/// a Phase 5 surface that renders an impersonated view without recording
/// one.
/// </summary>
public class OperatorImpersonationAuditTests
{
    [Fact]
    public async Task Every_SchoolAdmin_Surface_Enumeration_Writes_An_Audit_Row()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var auditor = new SchoolOperatorImpersonationAuditor(db);
        var tenantId = Guid.NewGuid();
        var operatorActorId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();

        var surfaces = new[]
        {
            SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
            SchoolOperatorImpersonationSurfaces.SchoolAdminRoster,
            SchoolOperatorImpersonationSurfaces.SchoolAdminClasses,
            SchoolOperatorImpersonationSurfaces.SchoolAdminExams,
            SchoolOperatorImpersonationSurfaces.SchoolAdminAnnouncements,
            SchoolOperatorImpersonationSurfaces.SchoolAdminReports,
            SchoolOperatorImpersonationSurfaces.SchoolAdminLicensing,
            SchoolOperatorImpersonationSurfaces.TeacherDashboard,
        };

        foreach (var surface in surfaces)
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                schoolTenantId: schoolTenantId,
                targetUserIdentityId: Guid.NewGuid(),
                surface: surface,
                reason: $"audit coverage for {surface}",
                correlationId: $"corr-{surface}");
        }

        var rows = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .Where(a => a.OperatorActorId == operatorActorId)
            .ToListAsync();

        Assert.Equal(surfaces.Length, rows.Count);
        foreach (var surface in surfaces)
        {
            Assert.Contains(rows, r => r.Surface == surface);
        }
    }

    [Fact]
    public async Task Audit_Row_Rejects_Empty_Reason()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var auditor = new SchoolOperatorImpersonationAuditor(db);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await auditor.RecordViewAsync(
                tenantId: Guid.NewGuid(),
                operatorActorId: Guid.NewGuid(),
                schoolTenantId: Guid.NewGuid(),
                targetUserIdentityId: Guid.NewGuid(),
                surface: SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
                reason: "   ",
                correlationId: "corr-empty"));
    }

    [Fact]
    public async Task Audit_Row_Rejects_Operator_Impersonating_Themselves()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var auditor = new SchoolOperatorImpersonationAuditor(db);
        var identity = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await auditor.RecordViewAsync(
                tenantId: Guid.NewGuid(),
                operatorActorId: identity,
                schoolTenantId: Guid.NewGuid(),
                targetUserIdentityId: identity,
                surface: SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
                reason: "self-audit attempt",
                correlationId: "corr-self"));
    }

    [Fact]
    public async Task Audit_Row_Captures_Operator_Identity_Not_Impersonated_Subject()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var auditor = new SchoolOperatorImpersonationAuditor(db);
        var operatorId = Guid.NewGuid();
        var impersonatedAdmin = Guid.NewGuid();

        await auditor.RecordViewAsync(
            tenantId: Guid.NewGuid(),
            operatorActorId: operatorId,
            schoolTenantId: Guid.NewGuid(),
            targetUserIdentityId: impersonatedAdmin,
            surface: SchoolOperatorImpersonationSurfaces.TeacherDashboard,
            reason: "quarterly on-call",
            correlationId: "corr-qtr");

        var row = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .SingleAsync();
        Assert.Equal(operatorId, row.OperatorActorId);
        Assert.NotEqual(impersonatedAdmin, row.OperatorActorId);
        Assert.Equal(impersonatedAdmin, row.TargetChildId);
    }
}
