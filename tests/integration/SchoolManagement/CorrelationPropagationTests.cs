using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.Parents;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement;

/// <summary>
/// T203 (Polish) — Correlation ID propagation.
///
/// Every cross-repo write must pair an outbox row, an audit row, and a
/// user-visible surface with the SAME <c>correlation_id</c> so on-call
/// can chase a single request through school-admin dashboards, reports,
/// and audit exports. This test seeds each of the three touch points and
/// asserts the identifier is preserved end-to-end:
///   1. A Phase 5 downstream event outbox row.
///   2. An operator-impersonation audit row for the same view.
///   3. The correlation id is identical across both rows, so when the
///      admin dashboard renders and the operator impersonation audit
///      writes, the trace identifiers match.
/// </summary>
public class CorrelationPropagationTests
{
    [Fact]
    public async Task Outbox_Row_Carries_Original_CorrelationId()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var correlationId = $"corr-{Guid.NewGuid():N}";

        await outbox.EnqueueAsync(
            Phase5DownstreamEventKind.school_created,
            Guid.NewGuid(),
            Guid.NewGuid(),
            payload: new { school = "alpha" },
            correlationId: correlationId);
        await db.SaveChangesAsync();

        var row = await db.Phase5DownstreamEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(correlationId, row.CorrelationId);
    }

    [Fact]
    public async Task Impersonation_Audit_Row_Preserves_The_Same_CorrelationId()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var auditor = new SchoolOperatorImpersonationAuditor(db);
        var correlationId = $"corr-{Guid.NewGuid():N}";

        await auditor.RecordViewAsync(
            tenantId: Guid.NewGuid(),
            operatorActorId: Guid.NewGuid(),
            schoolTenantId: Guid.NewGuid(),
            targetUserIdentityId: Guid.NewGuid(),
            surface: SchoolOperatorImpersonationSurfaces.SchoolAdminDashboard,
            reason: "on-call triage",
            correlationId: correlationId);

        var row = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .SingleAsync();
        Assert.Equal(correlationId, row.CorrelationId);
    }

    [Fact]
    public async Task Outbox_And_Audit_Share_The_Same_CorrelationId_Per_Request()
    {
        // A single request that both enqueues an outbox row AND writes an
        // impersonation audit row MUST carry the same correlation id on
        // both so a downstream log-search ties them together.
        await using var db = Phase5TestDbContextFactory.Create();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var auditor = new SchoolOperatorImpersonationAuditor(db);

        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        var operatorActorId = Guid.NewGuid();
        var correlationId = $"corr-{Guid.NewGuid():N}";

        await outbox.EnqueueAsync(
            Phase5DownstreamEventKind.report_generated,
            tenantId,
            schoolTenantId,
            payload: new { report = "mastery" },
            correlationId: correlationId);
        await auditor.RecordViewAsync(
            tenantId: tenantId,
            operatorActorId: operatorActorId,
            schoolTenantId: schoolTenantId,
            targetUserIdentityId: Guid.NewGuid(),
            surface: SchoolOperatorImpersonationSurfaces.SchoolAdminReports,
            reason: "report download triage",
            correlationId: correlationId);
        await db.SaveChangesAsync();

        var outboxCorr = await db.Phase5DownstreamEvents
            .IgnoreQueryFilters()
            .Select(e => e.CorrelationId)
            .SingleAsync();
        var auditCorr = await db.OperatorImpersonationAudits
            .IgnoreQueryFilters()
            .Select(a => a.CorrelationId)
            .SingleAsync();

        Assert.Equal(correlationId, outboxCorr);
        Assert.Equal(correlationId, auditCorr);
        Assert.Equal(outboxCorr, auditCorr);
    }

    [Fact]
    public async Task Different_Requests_Keep_Distinct_CorrelationIds()
    {
        // Two separate requests in the same tenant should NOT collapse
        // their correlation ids — this is the complementary invariant
        // so the earlier "share" test can't be satisfied by a constant.
        await using var db = Phase5TestDbContextFactory.Create();
        var outbox = new Phase5DownstreamEventOutbox(db);
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();

        await outbox.EnqueueAsync(
            Phase5DownstreamEventKind.roster_imported,
            tenantId,
            schoolTenantId,
            payload: new { count = 40 },
            correlationId: "corr-one");
        await outbox.EnqueueAsync(
            Phase5DownstreamEventKind.roster_imported,
            tenantId,
            schoolTenantId,
            payload: new { count = 10 },
            correlationId: "corr-two");
        await db.SaveChangesAsync();

        var corr = await db.Phase5DownstreamEvents
            .IgnoreQueryFilters()
            .Select(e => e.CorrelationId)
            .ToListAsync();
        Assert.Contains("corr-one", corr);
        Assert.Contains("corr-two", corr);
        Assert.Equal(2, corr.Distinct().Count());
    }
}
