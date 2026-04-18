using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Muallimi.Api.Billing.EntitlementEnforcement;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Polish;

/// <summary>
/// T128 (Polish) — Verify <see cref="EntitlementEnforcementMiddleware"/>
/// integrates with the downstream Phase 3–5 endpoints contract:
/// <c>ai_tutor_sessions, homework_help_images, mock_tests, whiteboard_sessions,
/// weekly_reports, detailed_analytics</c> (per entitlement-enforcement-contract.md).
///
/// The middleware resolves the current tenant's subscription snapshot and
/// attaches it to <see cref="HttpContext.Items"/> so downstream handlers can
/// gate feature access without each running its own subscription lookup.
/// These tests codify the invariant: whatever state the subscription is in
/// (trial, active, grace, expired, cancelled, or missing), the middleware
/// produces a deterministic snapshot the handler can branch on.
/// </summary>
public class EntitlementEnforcementMiddlewareTests
{
    private static HttpContext BuildContext(Guid? tenantId)
    {
        var ctx = new DefaultHttpContext();
        if (tenantId.HasValue)
        {
            ctx.Request.Headers["X-Tenant-Id"] = tenantId.Value.ToString();
        }
        return ctx;
    }

    private static async Task<EntitlementSnapshot?> InvokeAsync(
        HttpContext ctx,
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        var middleware = new EntitlementEnforcementMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx, db);
        return ctx.Items[EntitlementEnforcementMiddleware.HttpItemKey] as EntitlementSnapshot;
    }

    [Fact]
    public async Task Active_subscription_attaches_snapshot_for_downstream_gating()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        db.Subscriptions.Add(new Subscription
        {
            SubscriptionId = Guid.NewGuid(),
            TenantId = tenantId,
            PlanId = planId,
            PlanType = "family",
            Status = "active",
            CurrentPeriodStart = DateTime.UtcNow,
            CurrentPeriodEnd = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var snapshot = await InvokeAsync(BuildContext(tenantId), db);

        Assert.NotNull(snapshot);
        Assert.Equal(tenantId, snapshot!.TenantId);
        Assert.Equal("active", snapshot.SubscriptionStatus);
        Assert.Equal(planId, snapshot.PlanId);
    }

    [Fact]
    public async Task Trial_and_grace_states_round_trip_through_snapshot()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var trialTenant = Guid.NewGuid();
        var graceTenant = Guid.NewGuid();
        db.Subscriptions.AddRange(
            new Subscription
            {
                SubscriptionId = Guid.NewGuid(),
                TenantId = trialTenant,
                PlanId = Guid.NewGuid(),
                Status = "trial",
                CurrentPeriodStart = DateTime.UtcNow,
                CurrentPeriodEnd = DateTime.UtcNow.AddDays(14),
                TrialEnd = DateTime.UtcNow.AddDays(14),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new Subscription
            {
                SubscriptionId = Guid.NewGuid(),
                TenantId = graceTenant,
                PlanId = Guid.NewGuid(),
                Status = "grace",
                CurrentPeriodStart = DateTime.UtcNow.AddDays(-30),
                CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1),
                GracePeriodEnd = DateTime.UtcNow.AddDays(6),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var trial = await InvokeAsync(BuildContext(trialTenant), db);
        var grace = await InvokeAsync(BuildContext(graceTenant), db);

        Assert.Equal("trial", trial!.SubscriptionStatus);
        Assert.Equal("grace", grace!.SubscriptionStatus);
    }

    [Fact]
    public async Task Missing_subscription_produces_snapshot_with_null_status_for_free_tier_gating()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();

        var snapshot = await InvokeAsync(BuildContext(tenantId), db);

        Assert.NotNull(snapshot);
        Assert.Equal(tenantId, snapshot!.TenantId);
        Assert.Null(snapshot.SubscriptionStatus);
        Assert.Null(snapshot.PlanId);
    }

    [Fact]
    public async Task Expired_and_cancelled_states_surface_in_snapshot_so_handler_can_enforce_free_tier()
    {
        await using var db = Phase6TestDbContextFactory.Create();
        var expiredTenant = Guid.NewGuid();
        var cancelledTenant = Guid.NewGuid();
        db.Subscriptions.AddRange(
            new Subscription
            {
                SubscriptionId = Guid.NewGuid(),
                TenantId = expiredTenant,
                PlanId = Guid.NewGuid(),
                Status = "expired",
                CurrentPeriodStart = DateTime.UtcNow.AddDays(-60),
                CurrentPeriodEnd = DateTime.UtcNow.AddDays(-30),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new Subscription
            {
                SubscriptionId = Guid.NewGuid(),
                TenantId = cancelledTenant,
                PlanId = Guid.NewGuid(),
                Status = "cancelled",
                CurrentPeriodStart = DateTime.UtcNow.AddDays(-30),
                CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var expired = await InvokeAsync(BuildContext(expiredTenant), db);
        var cancelled = await InvokeAsync(BuildContext(cancelledTenant), db);

        Assert.Equal("expired", expired!.SubscriptionStatus);
        Assert.Equal("cancelled", cancelled!.SubscriptionStatus);
    }

    [Fact]
    public async Task Missing_tenant_header_skips_snapshot_attachment()
    {
        await using var db = Phase6TestDbContextFactory.Create();

        var snapshot = await InvokeAsync(BuildContext(tenantId: null), db);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Snapshot_is_scoped_to_single_tenant_only()
    {
        // The middleware must NEVER resolve another tenant's subscription —
        // cross-tenant entitlement leakage would let tenant A run tenant B's
        // premium features (e.g., whiteboard_sessions) without paying.
        await using var db = Phase6TestDbContextFactory.Create();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        db.Subscriptions.AddRange(
            new Subscription
            {
                SubscriptionId = Guid.NewGuid(),
                TenantId = tenantA,
                PlanId = Guid.NewGuid(),
                Status = "active",
                CurrentPeriodStart = DateTime.UtcNow,
                CurrentPeriodEnd = DateTime.UtcNow.AddDays(30),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new Subscription
            {
                SubscriptionId = Guid.NewGuid(),
                TenantId = tenantB,
                PlanId = Guid.NewGuid(),
                Status = "cancelled",
                CurrentPeriodStart = DateTime.UtcNow.AddDays(-60),
                CurrentPeriodEnd = DateTime.UtcNow.AddDays(-30),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        var snapshotA = await InvokeAsync(BuildContext(tenantA), db);
        var snapshotB = await InvokeAsync(BuildContext(tenantB), db);

        Assert.Equal("active", snapshotA!.SubscriptionStatus);
        Assert.Equal("cancelled", snapshotB!.SubscriptionStatus);
        Assert.NotEqual(snapshotA.PlanId, snapshotB.PlanId);
    }
}
