using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Billing.InvoiceGeneration;
using Muallimi.Api.Billing.SubscriptionLifecycle;
using Muallimi.Api.Payments;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Billing.BillingCycleEngine;

/// <summary>
/// T039 — Hourly background engine that advances subscription billing cycles.
///
/// For each subscription whose current period has ended, the engine generates
/// an invoice, triggers a charge via the PaymentProviderAdapter, and advances
/// the state machine: success → active + new period, failure → grace, grace
/// expired → expired. Emits Phase 6 operational events throughout.
/// </summary>
public sealed class BillingCycleEngineOptions
{
    public bool EnableBackgroundLoop { get; set; } = false;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

public interface IBillingCycleEngine
{
    Task<int> RunOnceAsync(CancellationToken ct = default);
}

public sealed class BillingCycleEngine : BackgroundService, IBillingCycleEngine
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BillingCycleEngine> _logger;
    private readonly BillingCycleEngineOptions _options;

    public BillingCycleEngine(
        IServiceProvider services,
        ILogger<BillingCycleEngine> logger,
        Microsoft.Extensions.Options.IOptions<BillingCycleEngineOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundLoop) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "BillingCycleEngine tick failed"); }
            await Task.Delay(_options.Interval, stoppingToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceGenerationService>();
        var payments = scope.ServiceProvider.GetRequiredService<IPaymentTransactionService>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<ISubscriptionLifecycleService>();

        var now = DateTime.UtcNow;
        var due = await db.Subscriptions
            .Where(s => (s.Status == "active" || s.Status == "trial") && s.CurrentPeriodEnd <= now)
            .Take(200)
            .ToListAsync(ct);

        foreach (var subscription in due)
        {
            var plan = await db.SubscriptionPlans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlanId == subscription.PlanId, ct);
            if (plan is null) continue;

            var correlationId = $"billing-cycle-{subscription.SubscriptionId:N}-{now:yyyyMMddHH}";
            var invoice = await invoices.CreateAsync(new InvoiceCreateInput(
                subscription.SubscriptionId,
                subscription.TenantId,
                subscription.CurrentPeriodStart,
                subscription.CurrentPeriodEnd,
                [new InvoiceLineItem(plan.PlanNameAr, plan.PlanNameEn, plan.PriceEgp, "egp")],
                "egp",
                TaxAmount: 0m), ct);

            if (plan.PriceEgp > 0 && !string.IsNullOrEmpty(subscription.PaymentMethodRef))
            {
                var txn = await payments.ChargeAsync(new ChargeCommand(
                    invoice.InvoiceId,
                    subscription.SubscriptionId,
                    subscription.TenantId,
                    plan.PriceEgp,
                    "egp",
                    subscription.PaymentMethodRef,
                    IdempotencyKey: $"cycle:{subscription.SubscriptionId:N}:{invoice.InvoiceNumber}",
                    correlationId), ct);

                if (txn.Status == "success")
                {
                    await invoices.MarkPaidAsync(invoice.InvoiceId, ct);
                    AdvancePeriod(db, subscription, plan, now);
                }
                else
                {
                    await invoices.MarkFailedAsync(invoice.InvoiceId, ct);
                    await lifecycle.EnterGraceAsync(subscription.SubscriptionId, correlationId, ct);
                }
            }
            else
            {
                // Free plan — no charge; simply roll the period forward.
                AdvancePeriod(db, subscription, plan, now);
            }
        }

        var graceExpired = await db.Subscriptions
            .Where(s => s.Status == "grace" && s.GracePeriodEnd != null && s.GracePeriodEnd <= now)
            .Take(200)
            .ToListAsync(ct);
        foreach (var subscription in graceExpired)
        {
            var correlationId = $"billing-cycle-expire-{subscription.SubscriptionId:N}-{now:yyyyMMddHH}";
            await lifecycle.ExpireAsync(subscription.SubscriptionId, correlationId, ct);
        }

        await db.SaveChangesAsync(ct);
        return due.Count + graceExpired.Count;
    }

    private static void AdvancePeriod(MuallimiDbContext db, Subscription subscription, SubscriptionPlan plan, DateTime now)
    {
        var prevEnd = subscription.CurrentPeriodEnd;
        var nextEnd = plan.BillingCycle == "yearly" ? prevEnd.AddYears(1) : prevEnd.AddMonths(1);
        subscription.CurrentPeriodStart = prevEnd;
        subscription.CurrentPeriodEnd = nextEnd;
        subscription.Status = "active";
        subscription.TrialEnd = null;
        subscription.UpdatedAt = now;
    }
}
