using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Billing;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Billing.SubscriptionLifecycle;

/// <summary>
/// T034 + T038 — Subscription state machine + lifecycle operations.
///
/// Transitions: trial → active (first successful payment), active → grace (payment
/// failure), grace → active (recovery), grace → expired (window exceeded),
/// active → cancelled (effective at period end), expired → active (re-subscribe).
/// </summary>
/// <param name="InitialStatus">
/// "trial" for direct/admin-created subscriptions; "pending_payment" when
/// the parent must complete a hosted checkout before the subscription activates.
/// </param>
public sealed record SubscriptionCreateInput(Guid TenantId, Guid PlanId, string? PaymentMethodRef, string CorrelationId, string InitialStatus = "pending_payment");
public sealed record SubscriptionPlanChangeResult(Guid SubscriptionId, Guid PreviousPlanId, Guid NewPlanId, DateTime EffectiveAt, decimal? ProratedChargeEgp);
public sealed record SubscriptionCancelResult(Guid SubscriptionId, DateTime EffectiveAt);

public interface ISubscriptionLifecycleService
{
    Task<Subscription> CreateAsync(SubscriptionCreateInput input, CancellationToken ct = default);
    Task<Subscription?> GetCurrentAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Transitions a pending_payment subscription to active after the provider
    /// confirms a successful payment via webhook.
    /// </summary>
    Task<Subscription?> ActivateFromPaymentAsync(Guid subscriptionId, string providerReference, string correlationId, CancellationToken ct = default);

    Task<SubscriptionPlanChangeResult?> ChangePlanAsync(Guid tenantId, Guid newPlanId, string correlationId, CancellationToken ct = default);
    Task<SubscriptionCancelResult?> CancelAsync(Guid tenantId, string? reason, string correlationId, CancellationToken ct = default);
    Task<Subscription?> EnterGraceAsync(Guid subscriptionId, string correlationId, CancellationToken ct = default);
    Task<Subscription?> RecoverFromGraceAsync(Guid subscriptionId, string correlationId, CancellationToken ct = default);
    Task<Subscription?> ExpireAsync(Guid subscriptionId, string correlationId, CancellationToken ct = default);
}

public sealed class SubscriptionLifecycleService : ISubscriptionLifecycleService
{
    private const int TrialDays = 14;
    private const int GraceDays = 7;

    private readonly MuallimiDbContext _db;
    private readonly Phase6OperationalEventOutbox _outbox;
    private readonly AuditTrailWriter _audit;
    private readonly IBillingNotificationDispatcher? _notifications;

    public SubscriptionLifecycleService(
        MuallimiDbContext db,
        Phase6OperationalEventOutbox outbox,
        AuditTrailWriter audit,
        IBillingNotificationDispatcher? notifications = null)
    {
        _db = db;
        _outbox = outbox;
        _audit = audit;
        _notifications = notifications;
    }

    public async Task<Subscription> CreateAsync(SubscriptionCreateInput input, CancellationToken ct = default)
    {
        var existing = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == input.TenantId, ct);
        if (existing is not null
            && existing.Status != "expired"
            && existing.Status != "cancelled"
            && existing.Status != "pending_payment")
            throw new InvalidOperationException($"Tenant {input.TenantId} already has a non-terminal subscription");

        // Replace a stale pending_payment record rather than stacking duplicates.
        if (existing?.Status == "pending_payment")
        {
            _db.Subscriptions.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }

        var plan = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == input.PlanId && p.IsActive, ct)
            ?? throw new InvalidOperationException($"Plan {input.PlanId} not found or inactive");

        var now = DateTime.UtcNow;
        var cycleEnd = plan.BillingCycle == "yearly" ? now.AddYears(1) : now.AddMonths(1);
        var allowedInitial = new HashSet<string> { "trial", "pending_payment" };
        if (!allowedInitial.Contains(input.InitialStatus))
            throw new ArgumentException($"Invalid initial status '{input.InitialStatus}'. Must be 'trial' or 'pending_payment'.");

        var subscription = new Subscription
        {
            SubscriptionId = Guid.NewGuid(),
            TenantId = input.TenantId,
            PlanId = plan.PlanId,
            PlanType = plan.PlanType,
            Status = input.InitialStatus,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = cycleEnd,
            TrialEnd = input.InitialStatus == "trial" ? now.AddDays(TrialDays) : null,
            PaymentMethodRef = input.PaymentMethodRef,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Subscriptions.Add(subscription);
        await _db.SaveChangesAsync(ct);

        await _outbox.EnqueueAsync(
            input.TenantId,
            "subscription_created",
            new { subscription_id = subscription.SubscriptionId, plan_id = plan.PlanId, tier = plan.Tier, billing_cycle = plan.BillingCycle },
            input.CorrelationId,
            ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = input.TenantId,
            ActorId = input.TenantId,
            ActorType = "tenant",
            TargetId = subscription.SubscriptionId,
            TargetType = "subscription",
            ActionType = "subscription.created",
            Payload = new { plan_id = plan.PlanId, payment_method_ref = input.PaymentMethodRef },
            CorrelationId = input.CorrelationId,
        }, ct);

        return subscription;
    }

    public Task<Subscription?> GetCurrentAsync(Guid tenantId, CancellationToken ct = default)
        => _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);

    public async Task<SubscriptionPlanChangeResult?> ChangePlanAsync(Guid tenantId, Guid newPlanId, string correlationId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (subscription is null) return null;
        if (subscription.Status is not ("trial" or "active" or "grace" or "pending_payment"))
            throw new InvalidOperationException($"Cannot change plan while subscription is {subscription.Status}");

        var currentPlan = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == subscription.PlanId, ct)
            ?? throw new InvalidOperationException("Current plan missing");
        var newPlan = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PlanId == newPlanId && p.IsActive, ct)
            ?? throw new InvalidOperationException($"Plan {newPlanId} not found or inactive");
        if (newPlan.PlanType != currentPlan.PlanType)
            throw new InvalidOperationException("Cannot change plan_type across tiers");

        var previousPlanId = subscription.PlanId;
        var now = DateTime.UtcNow;
        var isUpgrade = newPlan.PriceEgp > currentPlan.PriceEgp;
        DateTime effectiveAt;
        decimal? prorated = null;

        if (isUpgrade)
        {
            effectiveAt = now;
            var remaining = (decimal)Math.Max(0, (subscription.CurrentPeriodEnd - now).TotalDays);
            var cycleDays = currentPlan.BillingCycle == "yearly" ? 365m : 30m;
            prorated = Math.Round((newPlan.PriceEgp - currentPlan.PriceEgp) * (remaining / cycleDays), 2);
            subscription.PlanId = newPlan.PlanId;
        }
        else
        {
            effectiveAt = subscription.CurrentPeriodEnd;
            // downgrade applied at period end — record intended plan change via audit; actual switch handled by BillingCycleEngine
        }

        subscription.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = tenantId,
            ActorId = tenantId,
            ActorType = "tenant",
            TargetId = subscription.SubscriptionId,
            TargetType = "subscription",
            ActionType = isUpgrade ? "subscription.upgraded" : "subscription.downgrade_scheduled",
            Payload = new { previous_plan_id = previousPlanId, new_plan_id = newPlan.PlanId, effective_at = effectiveAt, prorated_charge_egp = prorated },
            CorrelationId = correlationId,
        }, ct);

        return new SubscriptionPlanChangeResult(subscription.SubscriptionId, previousPlanId, newPlan.PlanId, effectiveAt, prorated);
    }

    public async Task<SubscriptionCancelResult?> CancelAsync(Guid tenantId, string? reason, string correlationId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (subscription is null) return null;
        if (subscription.Status is "cancelled" or "expired")
            return new SubscriptionCancelResult(subscription.SubscriptionId, subscription.CurrentPeriodEnd);

        subscription.CancellationReason = reason;
        subscription.CancelledAt = DateTime.UtcNow;
        subscription.Status = "cancelled";
        subscription.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = tenantId,
            ActorId = tenantId,
            ActorType = "tenant",
            TargetId = subscription.SubscriptionId,
            TargetType = "subscription",
            ActionType = "subscription.cancelled",
            Payload = new { reason, effective_at = subscription.CurrentPeriodEnd },
            CorrelationId = correlationId,
        }, ct);

        return new SubscriptionCancelResult(subscription.SubscriptionId, subscription.CurrentPeriodEnd);
    }

    public async Task<Subscription?> ActivateFromPaymentAsync(Guid subscriptionId, string providerReference, string correlationId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct);
        if (subscription is null) return null;
        if (subscription.Status == "active") return subscription; // idempotent

        var now = DateTime.UtcNow;
        subscription.Status = "active";
        subscription.PaymentMethodRef = providerReference;
        subscription.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        await _outbox.EnqueueAsync(
            subscription.TenantId,
            "subscription_activated",
            new { subscription_id = subscription.SubscriptionId, provider_reference = providerReference },
            correlationId, ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = subscription.TenantId,
            ActorId = subscription.TenantId,
            ActorType = "payment_webhook",
            TargetId = subscription.SubscriptionId,
            TargetType = "subscription",
            ActionType = "subscription.activated",
            Payload = new { provider_reference = providerReference },
            CorrelationId = correlationId,
        }, ct);

        return subscription;
    }

    public async Task<Subscription?> EnterGraceAsync(Guid subscriptionId, string correlationId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct);
        if (subscription is null) return null;
        if (subscription.Status is not ("active" or "trial")) return subscription;

        subscription.Status = "grace";
        subscription.GracePeriodEnd = DateTime.UtcNow.AddDays(GraceDays);
        subscription.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _outbox.EnqueueAsync(subscription.TenantId, "subscription_entered_grace",
            new { subscription_id = subscription.SubscriptionId, grace_period_end = subscription.GracePeriodEnd }, correlationId, ct);
        if (_notifications is not null && subscription.GracePeriodEnd is not null)
        {
            await _notifications.DispatchGracePeriodStartedAsync(
                new BillingNotificationContext(
                    TenantId: subscription.TenantId,
                    RecipientId: subscription.TenantId,
                    NotificationId: subscription.SubscriptionId,
                    Language: "ar",
                    CorrelationId: correlationId),
                subscription.GracePeriodEnd.Value, ct);
        }
        return subscription;
    }

    public async Task<Subscription?> RecoverFromGraceAsync(Guid subscriptionId, string correlationId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct);
        if (subscription is null) return null;
        if (subscription.Status != "grace") return subscription;
        subscription.Status = "active";
        subscription.GracePeriodEnd = null;
        subscription.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _outbox.EnqueueAsync(subscription.TenantId, "subscription_recovered",
            new { subscription_id = subscription.SubscriptionId }, correlationId, ct);
        return subscription;
    }

    public async Task<Subscription?> ExpireAsync(Guid subscriptionId, string correlationId, CancellationToken ct = default)
    {
        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.SubscriptionId == subscriptionId, ct);
        if (subscription is null) return null;
        if (subscription.Status == "expired") return subscription;
        subscription.Status = "expired";
        subscription.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _outbox.EnqueueAsync(subscription.TenantId, "subscription_expired",
            new { subscription_id = subscription.SubscriptionId }, correlationId, ct);
        if (_notifications is not null)
        {
            await _notifications.DispatchSubscriptionExpiredAsync(
                new BillingNotificationContext(
                    TenantId: subscription.TenantId,
                    RecipientId: subscription.TenantId,
                    NotificationId: subscription.SubscriptionId,
                    Language: "ar",
                    CorrelationId: correlationId), ct);
        }
        return subscription;
    }
}
