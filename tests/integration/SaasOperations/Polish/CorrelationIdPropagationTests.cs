using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Billing;
using Muallimi.Api.Billing.SubscriptionLifecycle;
using Muallimi.Api.Billing.SubscriptionPlans;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Observability.StructuredLogging;
using Muallimi.Domain.SaasOperations;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Polish;

/// <summary>
/// T130 (Polish) — Correlation ID propagation end-to-end.
///
/// The four-repo trace invariant: a single request reaches
/// <c>frontend → main-backend → ai-service → document-ingestion</c> and every
/// log/outbox/audit row emitted downstream must carry the SAME
/// <c>correlation_id</c> so on-call can reconstruct the flow from one
/// identifier.
///
/// The three touch points for main-backend are:
///   1. Serilog enrichment (reads the incoming <c>X-Correlation-Id</c> header).
///   2. The Phase 6 operational outbox rows.
///   3. The audit-trail rows.
/// This suite asserts all three stay synchronised under a single request.
/// </summary>
public class CorrelationIdPropagationTests
{
    private sealed class CapturingFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false)
            => new LogEventProperty(name, new ScalarValue(value));
    }

    private static LogEvent MakeEvent()
        => new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            messageTemplate: new MessageTemplate("test", Enumerable.Empty<MessageTemplateToken>()),
            properties: Array.Empty<LogEventProperty>());

    [Fact]
    public void StructuredLoggingEnricher_reads_correlation_id_from_inbound_header()
    {
        var accessor = new HttpContextAccessor();
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-Correlation-Id"] = "corr-abc-123";
        ctx.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        accessor.HttpContext = ctx;

        var enricher = new StructuredLoggingEnricher(accessor);
        var evt = MakeEvent();
        enricher.Enrich(evt, new CapturingFactory());

        Assert.Equal("main-backend", (evt.Properties["service_name"] as ScalarValue)!.Value);
        Assert.Equal("corr-abc-123", (evt.Properties["correlation_id"] as ScalarValue)!.Value);
        Assert.True(evt.Properties.ContainsKey("tenant_id"));
    }

    [Fact]
    public void StructuredLoggingEnricher_falls_back_to_trace_identifier_when_header_missing()
    {
        var accessor = new HttpContextAccessor();
        var ctx = new DefaultHttpContext { TraceIdentifier = "trace-fallback-7" };
        accessor.HttpContext = ctx;

        var enricher = new StructuredLoggingEnricher(accessor);
        var evt = MakeEvent();
        enricher.Enrich(evt, new CapturingFactory());

        Assert.Equal("trace-fallback-7", (evt.Properties["correlation_id"] as ScalarValue)!.Value);
    }

    [Fact]
    public async Task Subscription_create_writes_outbox_and_audit_rows_with_matching_correlation_id()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var outbox = new Phase6OperationalEventOutbox(db);
        var audit = new AuditTrailWriter(db);
        var svc = new SubscriptionLifecycleService(db, outbox, audit);

        var plan = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(),
            PlanNameAr = "خطة",
            PlanNameEn = "Plan",
            PlanType = "family",
            Tier = "standard",
            PriceEgp = 99m,
            BillingCycle = "monthly",
            FeatureEntitlements = "{}",
            UsageLimits = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync();

        var correlationId = $"corr-{Guid.NewGuid():N}";
        var tenantId = Guid.NewGuid();
        await svc.CreateAsync(new SubscriptionCreateInput(tenantId, plan.PlanId, "pm_test", correlationId));

        var outboxRow = await db.Phase6OperationalEvents.SingleAsync(e => e.EventKind == "subscription_created");
        var auditRow = await db.AuditEntries.SingleAsync(a => a.ActionType == "subscription.created");

        Assert.Equal(correlationId, outboxRow.CorrelationId);
        Assert.Equal(correlationId, auditRow.CorrelationId);
        Assert.Equal(outboxRow.CorrelationId, auditRow.CorrelationId);
    }

    [Fact]
    public async Task Two_distinct_requests_produce_distinct_correlation_ids_on_both_sinks()
    {
        // Complement to the "share" test — distinct correlation ids must
        // remain distinct so the "match" invariant cannot be satisfied by
        // a constant fallback.
        await using var db = Phase6TestDbContextFactory.Create();
        var outbox = new Phase6OperationalEventOutbox(db);
        var audit = new AuditTrailWriter(db);
        var svc = new SubscriptionLifecycleService(db, outbox, audit);

        var plan = new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(),
            PlanNameAr = "خطة",
            PlanNameEn = "Plan",
            PlanType = "family",
            Tier = "standard",
            PriceEgp = 99m,
            BillingCycle = "monthly",
            FeatureEntitlements = "{}",
            UsageLimits = "{}",
            IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.SubscriptionPlans.Add(plan);
        await db.SaveChangesAsync();

        await svc.CreateAsync(new SubscriptionCreateInput(Guid.NewGuid(), plan.PlanId, "pm_a", "corr-a"));
        await svc.CreateAsync(new SubscriptionCreateInput(Guid.NewGuid(), plan.PlanId, "pm_b", "corr-b"));

        var outboxCorr = db.Phase6OperationalEvents
            .Where(e => e.EventKind == "subscription_created")
            .Select(e => e.CorrelationId).ToList();
        Assert.Contains("corr-a", outboxCorr);
        Assert.Contains("corr-b", outboxCorr);

        var auditCorr = db.AuditEntries
            .Where(a => a.ActionType == "subscription.created")
            .Select(a => a.CorrelationId).ToList();
        Assert.Contains("corr-a", auditCorr);
        Assert.Contains("corr-b", auditCorr);
    }
}
