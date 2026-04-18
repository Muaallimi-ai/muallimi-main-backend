using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Compliance;

/// <summary>
/// T115 — Audit trail query service contract: keyset pagination, filters, and
/// retrieval by id. Exercises <see cref="AuditTrailQueryService"/> against the
/// shared Phase 6 in-memory harness.
/// </summary>
public class AuditTrailQueryTests
{
    [Fact]
    public async Task Query_filters_by_tenant_actor_and_action()
    {
        using var db = Phase6TestDbContextFactory.Create();
        var tenant = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.AuditEntries.AddRange(
            Entry(tenant, actor, "operator.feature_flag.toggled", now.AddMinutes(-1)),
            Entry(tenant, actor, "subscription.cancelled", now.AddMinutes(-2)),
            Entry(Guid.NewGuid(), actor, "operator.feature_flag.toggled", now.AddMinutes(-3)));
        await db.SaveChangesAsync();

        var svc = new AuditTrailQueryService(db);
        var result = await svc.QueryAsync(new AuditTrailQuery
        {
            TenantId = tenant,
            ActorId = actor,
            ActionTypes = new[] { "operator.feature_flag.toggled" },
        });

        Assert.Single(result.Entries);
        Assert.Equal("operator.feature_flag.toggled", result.Entries[0].ActionType);
    }

    [Fact]
    public async Task Query_pages_via_keyset_cursor_without_duplicates()
    {
        using var db = Phase6TestDbContextFactory.Create();
        var tenant = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            db.AuditEntries.Add(Entry(tenant, actor, "subscription.created", baseTime.AddMinutes(-i)));
        }
        await db.SaveChangesAsync();

        var svc = new AuditTrailQueryService(db);
        var page1 = await svc.QueryAsync(new AuditTrailQuery { TenantId = tenant, Limit = 2 });
        Assert.Equal(2, page1.Entries.Count);
        Assert.NotNull(page1.NextCursor);

        var page2 = await svc.QueryAsync(new AuditTrailQuery
        {
            TenantId = tenant,
            Limit = 2,
            Cursor = page1.NextCursor,
        });
        Assert.Equal(2, page2.Entries.Count);
        Assert.DoesNotContain(page1.Entries.Select(e => e.AuditEntryId), id => page2.Entries.Any(p => p.AuditEntryId == id));

        var page3 = await svc.QueryAsync(new AuditTrailQuery
        {
            TenantId = tenant,
            Limit = 2,
            Cursor = page2.NextCursor,
        });
        Assert.Single(page3.Entries);
        Assert.Null(page3.NextCursor);
    }

    [Fact]
    public async Task GetById_returns_null_when_missing()
    {
        using var db = Phase6TestDbContextFactory.Create();
        var svc = new AuditTrailQueryService(db);
        var result = await svc.GetByIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public void Cursor_encoding_roundtrips_through_base64()
    {
        var cursor = new AuditTrailCursor(DateTime.UtcNow, Guid.NewGuid());
        var encoded = cursor.Encode();
        var decoded = AuditTrailCursor.TryDecode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(cursor.AuditEntryId, decoded!.AuditEntryId);
        Assert.Equal(cursor.OccurredAt.ToUniversalTime(), decoded.OccurredAt.ToUniversalTime());
    }

    private static AuditEntry Entry(Guid tenant, Guid actor, string action, DateTime at) => new()
    {
        AuditEntryId = Guid.NewGuid(),
        TenantId = tenant,
        ActorId = actor,
        ActorType = "operator",
        ActionType = action,
        CorrelationId = $"corr-{action}",
        OccurredAt = at,
    };
}
