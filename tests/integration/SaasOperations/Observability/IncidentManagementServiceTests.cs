using Muallimi.Api.AiOperations.IncidentManagement;
using Muallimi.Api.Compliance.AuditTrail;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Observability;

/// <summary>
/// T080 + T087 — Verifies the incident lifecycle state machine and the
/// audit trail records created at each transition.
/// </summary>
public class IncidentManagementServiceTests
{
    private static IncidentManagementService CreateService(out Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        db = Phase6TestDbContextFactory.Create();
        return new IncidentManagementService(db, new AuditTrailWriter(db));
    }

    [Fact]
    public async Task Create_persists_open_incident_and_writes_audit_entry()
    {
        var service = CreateService(out var db);

        var incident = await service.CreateAsync(new IncidentCreateCommand(
            Severity: "high",
            Title: "Billing worker stuck",
            Description: "Charge loop stalled",
            AffectedServices: new[] { "main-backend" },
            AffectedTenants: null,
            CorrelationId: "corr-us4-1",
            RunbookReference: null,
            OpenedBy: Guid.NewGuid()));

        Assert.Equal("open", incident.Status);
        Assert.Equal("corr-us4-1", incident.CorrelationId);
        Assert.Single(db.IncidentRecords);
        Assert.Contains(db.AuditEntries, a => a.ActionType == "incident.created" && a.TargetId == incident.IncidentId);
    }

    [Fact]
    public async Task Update_valid_status_transition_writes_audit_and_timestamp()
    {
        var service = CreateService(out var db);
        var actor = Guid.NewGuid();
        var incident = await service.CreateAsync(new IncidentCreateCommand(
            "high", "X", null, Array.Empty<string>(), null, null, null, actor));

        var investigating = await service.UpdateAsync(incident.IncidentId,
            new IncidentUpdateCommand("investigating", null, null, null, null, actor));
        Assert.NotNull(investigating);
        Assert.Equal("investigating", investigating!.Status);

        var mitigated = await service.UpdateAsync(incident.IncidentId,
            new IncidentUpdateCommand("mitigated", null, null, null, null, actor));
        Assert.NotNull(mitigated!.MitigatedAt);

        var resolved = await service.UpdateAsync(incident.IncidentId,
            new IncidentUpdateCommand("resolved", "root cause X", "rolled back", null, null, actor));
        Assert.Equal("resolved", resolved!.Status);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal("root cause X", resolved.RootCause);

        Assert.Contains(db.AuditEntries, a => a.ActionType == "incident.resolved");
    }

    [Fact]
    public async Task Update_invalid_status_transition_throws()
    {
        var service = CreateService(out var db);
        var actor = Guid.NewGuid();
        var incident = await service.CreateAsync(new IncidentCreateCommand(
            "medium", "Y", null, Array.Empty<string>(), null, null, null, actor));

        await service.UpdateAsync(incident.IncidentId,
            new IncidentUpdateCommand("resolved", null, null, null, null, actor));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(incident.IncidentId,
                new IncidentUpdateCommand("investigating", null, null, null, null, actor)));
    }

    [Fact]
    public async Task List_applies_status_and_severity_filters()
    {
        var service = CreateService(out var db);
        var actor = Guid.NewGuid();
        await service.CreateAsync(new IncidentCreateCommand("critical", "A", null, null, null, null, null, actor));
        await service.CreateAsync(new IncidentCreateCommand("low", "B", null, null, null, null, null, actor));

        var (critical, _) = await service.ListAsync(new IncidentQuery(null, "critical", null, 20));
        Assert.Single(critical);
        Assert.Equal("A", critical[0].Title);

        var (openOnly, _) = await service.ListAsync(new IncidentQuery("open", null, null, 20));
        Assert.Equal(2, openOnly.Count);
    }
}
