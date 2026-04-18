using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.OperatorManagement.Impersonation;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.OperatorManagement;

public class ImpersonationServiceTests
{
    [Fact]
    public async Task StartAsync_issues_token_and_writes_audit_entry()
    {
        var db = Phase6TestDbContextFactory.Create();
        var svc = new ImpersonationService(new AuditTrailWriter(db));

        var operatorId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var result = await svc.StartAsync(
            operatorId, tenantId, "parent", targetUserId: null,
            reason: "customer-support-ticket-123",
            correlationId: "corr-start",
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.Token));
        Assert.True(result.ExpiresAt > DateTime.UtcNow);
        var audits = await db.AuditEntries.ToListAsync();
        Assert.Single(audits, a => a.ActionType == "operator.impersonation.started");
    }

    [Fact]
    public async Task EndAsync_records_duration_and_audit_entry()
    {
        var db = Phase6TestDbContextFactory.Create();
        var svc = new ImpersonationService(new AuditTrailWriter(db));
        var start = await svc.StartAsync(
            Guid.NewGuid(), Guid.NewGuid(), "school_admin", null, "incident-rca",
            "corr-end", CancellationToken.None);

        var end = await svc.EndAsync(start.Token, "corr-end", CancellationToken.None);

        Assert.NotNull(end);
        Assert.True(end!.DurationSeconds >= 0);
        var audits = await db.AuditEntries.ToListAsync();
        Assert.Single(audits, a => a.ActionType == "operator.impersonation.ended");
    }

    [Fact]
    public async Task EndAsync_returns_null_when_token_unknown()
    {
        var db = Phase6TestDbContextFactory.Create();
        var svc = new ImpersonationService(new AuditTrailWriter(db));
        var result = await svc.EndAsync("not-a-real-token", "corr", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task StartAsync_rejects_empty_reason()
    {
        var db = Phase6TestDbContextFactory.Create();
        var svc = new ImpersonationService(new AuditTrailWriter(db));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.StartAsync(
            Guid.NewGuid(), Guid.NewGuid(), "parent", null, " ",
            "corr", CancellationToken.None));
    }
}
