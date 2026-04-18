using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.Compliance.DataRetention;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SaasOperations;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Compliance;

/// <summary>
/// T117 + T119 — DataRetentionService applies delete / anonymise / archive
/// rules to aged records and DefaultRetentionPolicySeeder populates the
/// default 9-policy catalogue per data-retention-contract.md.
/// </summary>
public class DataRetentionServiceTests
{
    [Fact]
    public async Task Seeder_populates_nine_default_policies()
    {
        using var db = Phase6TestDbContextFactory.Create();
        await DefaultRetentionPolicySeeder.EnsureSeededAsync(db);
        var rows = await db.DataRetentionPolicies.ToListAsync();
        Assert.Equal(9, rows.Count);
        Assert.Contains(rows, r => r.EntityType == "session_event" && r.AnonymisationRule == "anonymise" && r.RetentionDays == 365);
        Assert.Contains(rows, r => r.EntityType == "audit_entry" && r.AnonymisationRule == "archive" && r.RetentionDays == 2555);
        Assert.Contains(rows, r => r.EntityType == "dead_letter_message" && r.AnonymisationRule == "delete" && r.RetentionDays == 30);
    }

    [Fact]
    public async Task Seeder_is_idempotent_on_reruns()
    {
        using var db = Phase6TestDbContextFactory.Create();
        await DefaultRetentionPolicySeeder.EnsureSeededAsync(db);
        await DefaultRetentionPolicySeeder.EnsureSeededAsync(db);
        var count = await db.DataRetentionPolicies.CountAsync();
        Assert.Equal(9, count);
    }

    [Fact]
    public async Task Execute_deletes_expired_dead_letter_messages_and_writes_audit_entry()
    {
        using var db = Phase6TestDbContextFactory.Create();
        await DefaultRetentionPolicySeeder.EnsureSeededAsync(db);

        db.ProgressIngestionDeadLetters.AddRange(
            new ProgressIngestionDeadLetter
            {
                DeadLetterId = Guid.NewGuid(),
                SourceEventId = "e1",
                EventKind = "session",
                Reason = "timeout",
                RecordedAt = DateTime.UtcNow.AddDays(-45),
            },
            new ProgressIngestionDeadLetter
            {
                DeadLetterId = Guid.NewGuid(),
                SourceEventId = "e2",
                EventKind = "session",
                Reason = "timeout",
                RecordedAt = DateTime.UtcNow.AddDays(-5),
            });
        await db.SaveChangesAsync();

        var svc = new DataRetentionService(db, new AuditTrailWriter(db));
        var result = await svc.ExecuteAsync(Guid.NewGuid(), "corr-retention-1");

        Assert.True(result.RowsAffected >= 1);
        Assert.Equal(9, result.PoliciesEvaluated);
        Assert.Equal(1, await db.ProgressIngestionDeadLetters.CountAsync());

        var audits = await db.AuditEntries.Where(a => a.ActionType == "data_retention.executed").ToListAsync();
        Assert.Equal(9, audits.Count);
    }

    [Fact]
    public async Task Execute_anonymises_expired_session_events()
    {
        using var db = Phase6TestDbContextFactory.Create();
        await DefaultRetentionPolicySeeder.EnsureSeededAsync(db);

        db.SessionEvents.Add(new SessionEvent
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            StudentSessionId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            EventKind = "lesson_started",
            EventPayload = "{\"studentId\":\"abc\"}",
            CurriculumScope = "{\"grade\":9}",
            CreatedAt = DateTime.UtcNow.AddDays(-400),
        });
        await db.SaveChangesAsync();

        var svc = new DataRetentionService(db, new AuditTrailWriter(db));
        await svc.ExecuteAsync(Guid.NewGuid(), "corr-retention-2");

        var row = await db.SessionEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("{\"anonymised\":true}", row.EventPayload);
    }

    [Fact]
    public async Task Execute_does_not_touch_records_within_retention_window()
    {
        using var db = Phase6TestDbContextFactory.Create();
        await DefaultRetentionPolicySeeder.EnsureSeededAsync(db);

        db.Phase6AIOperationsMetrics.Add(new AIOperationsMetric
        {
            MetricId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            PromptKey = "tutor.answer",
            PromptVersion = "1.0",
            ProviderName = "stub",
            CorrelationId = "corr-keep",
            OccurredAt = DateTime.UtcNow.AddDays(-10),
        });
        await db.SaveChangesAsync();
        var before = await db.Phase6AIOperationsMetrics.CountAsync();

        var svc = new DataRetentionService(db, new AuditTrailWriter(db));
        await svc.ExecuteAsync(Guid.NewGuid(), "corr-retention-3");

        var after = await db.Phase6AIOperationsMetrics.CountAsync();
        Assert.Equal(before, after);
    }
}
