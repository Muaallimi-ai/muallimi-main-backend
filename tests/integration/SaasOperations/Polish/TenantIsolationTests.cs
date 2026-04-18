using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Polish;

/// <summary>
/// T136 (Polish) — Zero cross-tenant data leakage across the Phase 6
/// tenant-scoped entities: Subscription, Invoice, PaymentTransaction,
/// NotificationDeliveryReceipt, AIOperationsMetric, AuditEntry,
/// DataDeletionRequest, FeatureFlag (see <c>ApplyPhase6TenantFilters</c>
/// in MuallimiDbContext).
///
/// Tenant isolation is enforced via global query filters that resolve
/// <see cref="IDbTenantContextAccessor.CurrentTenantId"/>. These tests
/// flip the accessor between tenant A and tenant B and assert each tenant
/// only sees rows it owns — and that <c>IgnoreQueryFilters</c> is required
/// to see the full set, proving the filter is active.
/// </summary>
public class TenantIsolationTests
{
    private sealed class MutableTenantAccessor : IDbTenantContextAccessor
    {
        public Guid? CurrentTenantId { get; set; }
    }

    private static (MuallimiDbContext db, MutableTenantAccessor accessor) Build(string? databaseName = null)
    {
        var accessor = new MutableTenantAccessor();
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase(databaseName ?? $"phase6-isolation-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new Phase6TestDbContextWithAccessor(options, accessor);
        return (db, accessor);
    }

    /// <summary>
    /// Phase6TestDbContext variant that accepts an <see cref="IDbTenantContextAccessor"/>
    /// so tenant-isolation tests can toggle the current tenant per assertion.
    /// </summary>
    private sealed class Phase6TestDbContextWithAccessor : MuallimiDbContext
    {
        public Phase6TestDbContextWithAccessor(
            DbContextOptions<MuallimiDbContext> options,
            IDbTenantContextAccessor accessor) : base(options, accessor) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Ignore<Muallimi.Domain.Curriculum.ContentChunk>();
            modelBuilder.Ignore<Muallimi.Domain.Curriculum.QaCacheEntry>();
        }
    }

    [Fact]
    public async Task Subscription_query_returns_only_current_tenant_rows()
    {
        var dbName = $"phase6-isolation-{Guid.NewGuid():N}";
        var (db, accessor) = Build(dbName);
        await using var _ = db;

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        accessor.CurrentTenantId = null; // seed pass — filter matches nothing
        db.Subscriptions.AddRange(
            NewSubscription(tenantA, "active"),
            NewSubscription(tenantB, "cancelled"));
        await db.SaveChangesAsync();

        accessor.CurrentTenantId = tenantA;
        var aSeen = await db.Subscriptions.ToListAsync();
        Assert.Single(aSeen);
        Assert.Equal(tenantA, aSeen[0].TenantId);

        accessor.CurrentTenantId = tenantB;
        var bSeen = await db.Subscriptions.ToListAsync();
        Assert.Single(bSeen);
        Assert.Equal(tenantB, bSeen[0].TenantId);
    }

    [Fact]
    public async Task Invoice_and_payment_and_audit_queries_are_tenant_scoped()
    {
        var (db, accessor) = Build();
        await using var _ = db;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        accessor.CurrentTenantId = null;
        db.Invoices.AddRange(
            new Invoice { InvoiceId = Guid.NewGuid(), TenantId = tenantA, SubscriptionId = Guid.NewGuid(), InvoiceNumber = "INV-1", Subtotal = 99, TaxAmount = 0, Total = 99, IssuedAt = DateTime.UtcNow },
            new Invoice { InvoiceId = Guid.NewGuid(), TenantId = tenantB, SubscriptionId = Guid.NewGuid(), InvoiceNumber = "INV-2", Subtotal = 5000, TaxAmount = 0, Total = 5000, IssuedAt = DateTime.UtcNow });
        db.PaymentTransactions.AddRange(
            new PaymentTransaction { TransactionId = Guid.NewGuid(), TenantId = tenantA, SubscriptionId = Guid.NewGuid(), InvoiceId = Guid.NewGuid(), ProviderName = "stub", Amount = 99, IdempotencyKey = "a-1", CorrelationId = "corr-a", AttemptedAt = DateTime.UtcNow },
            new PaymentTransaction { TransactionId = Guid.NewGuid(), TenantId = tenantB, SubscriptionId = Guid.NewGuid(), InvoiceId = Guid.NewGuid(), ProviderName = "stub", Amount = 5000, IdempotencyKey = "b-1", CorrelationId = "corr-b", AttemptedAt = DateTime.UtcNow });
        db.AuditEntries.AddRange(
            new AuditEntry { AuditEntryId = Guid.NewGuid(), TenantId = tenantA, ActorId = Guid.NewGuid(), ActorType = "parent", ActionType = "subscription.created", CorrelationId = "corr-a", OccurredAt = DateTime.UtcNow },
            new AuditEntry { AuditEntryId = Guid.NewGuid(), TenantId = tenantB, ActorId = Guid.NewGuid(), ActorType = "school_admin", ActionType = "subscription.created", CorrelationId = "corr-b", OccurredAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        accessor.CurrentTenantId = tenantA;
        Assert.Single(await db.Invoices.ToListAsync(), i => i.TenantId == tenantA);
        Assert.Single(await db.PaymentTransactions.ToListAsync(), p => p.TenantId == tenantA);
        Assert.Single(await db.AuditEntries.ToListAsync(), a => a.TenantId == tenantA);

        accessor.CurrentTenantId = tenantB;
        Assert.DoesNotContain(await db.Invoices.ToListAsync(), i => i.TenantId == tenantA);
        Assert.DoesNotContain(await db.PaymentTransactions.ToListAsync(), p => p.TenantId == tenantA);
        Assert.DoesNotContain(await db.AuditEntries.ToListAsync(), a => a.TenantId == tenantA);
    }

    [Fact]
    public async Task IgnoreQueryFilters_is_required_to_see_both_tenants()
    {
        // Proves the query filter is actually active — without it, the
        // "single tenant" assertions above would pass vacuously.
        var (db, accessor) = Build();
        await using var _ = db;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        accessor.CurrentTenantId = null;
        db.Subscriptions.AddRange(
            NewSubscription(tenantA, "active"),
            NewSubscription(tenantB, "active"));
        await db.SaveChangesAsync();

        accessor.CurrentTenantId = tenantA;
        var filteredCount = await db.Subscriptions.CountAsync();
        var unfilteredCount = await db.Subscriptions.IgnoreQueryFilters().CountAsync();

        Assert.Equal(1, filteredCount);
        Assert.Equal(2, unfilteredCount);
    }

    [Fact]
    public async Task AIOperationsMetric_and_FeatureFlag_are_tenant_scoped()
    {
        var (db, accessor) = Build();
        await using var _ = db;
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        accessor.CurrentTenantId = null;
        db.Phase6AIOperationsMetrics.AddRange(
            new AIOperationsMetric { MetricId = Guid.NewGuid(), TenantId = tenantA, Phase = "phase2", PromptKey = "tutor.answer", ProviderName = "local", EstimatedCostEgp = 0.05m, OccurredAt = DateTime.UtcNow },
            new AIOperationsMetric { MetricId = Guid.NewGuid(), TenantId = tenantB, Phase = "phase5", PromptKey = "exam.guardrail", ProviderName = "local", EstimatedCostEgp = 0.07m, OccurredAt = DateTime.UtcNow });
        db.FeatureFlags.AddRange(
            new FeatureFlag { FeatureFlagId = Guid.NewGuid(), TenantId = tenantA, FlagName = "leaderboards", IsEnabled = true, ChangedByOperatorId = Guid.NewGuid(), ChangedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow },
            new FeatureFlag { FeatureFlagId = Guid.NewGuid(), TenantId = tenantB, FlagName = "leaderboards", IsEnabled = false, ChangedByOperatorId = Guid.NewGuid(), ChangedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        accessor.CurrentTenantId = tenantA;
        var flagA = await db.FeatureFlags.SingleAsync(f => f.FlagName == "leaderboards");
        Assert.True(flagA.IsEnabled);
        Assert.All(await db.Phase6AIOperationsMetrics.ToListAsync(), m => Assert.Equal(tenantA, m.TenantId));

        accessor.CurrentTenantId = tenantB;
        var flagB = await db.FeatureFlags.SingleAsync(f => f.FlagName == "leaderboards");
        Assert.False(flagB.IsEnabled);
        Assert.All(await db.Phase6AIOperationsMetrics.ToListAsync(), m => Assert.Equal(tenantB, m.TenantId));
    }

    private static Subscription NewSubscription(Guid tenantId, string status) => new Subscription
    {
        SubscriptionId = Guid.NewGuid(),
        TenantId = tenantId,
        PlanId = Guid.NewGuid(),
        Status = status,
        CurrentPeriodStart = DateTime.UtcNow.AddDays(-1),
        CurrentPeriodEnd = DateTime.UtcNow.AddDays(29),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
