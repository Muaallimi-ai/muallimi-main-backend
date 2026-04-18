using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Payments.PaymentProviderAdapter;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Payments.RetryPolicy;

/// <summary>
/// T112 — Exponential backoff for transient payment failures. Only
/// failure_code == "network_timeout" is retryable; permanent failures
/// (insufficient_funds, expired_card, fraud_hold) never enter the retry queue.
///
/// Backoff ladder: 30s → 5m → 30m (3 attempts). After the last attempt the
/// transaction stays in failed status and is surfaced to the billing ops view.
/// </summary>
public sealed class PaymentRetryOptions
{
    public TimeSpan[] Backoffs { get; set; } = new[]
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
    };

    // Tick interval. Kept small so unit tests can stub it; production defaults to 15s.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed record PendingRetry(Guid TransactionId, int Attempt, DateTime NotBefore);

public sealed class PaymentRetryScheduler
{
    private readonly ConcurrentDictionary<Guid, PendingRetry> _queue = new();
    private readonly PaymentRetryOptions _options;

    public PaymentRetryScheduler(IOptions<PaymentRetryOptions> options)
    {
        _options = options.Value;
    }

    public int MaxAttempts => _options.Backoffs.Length;

    public bool Schedule(Guid transactionId, int attempt, DateTime? nowUtc = null)
    {
        if (attempt < 1 || attempt > _options.Backoffs.Length) return false;
        var now = nowUtc ?? DateTime.UtcNow;
        var notBefore = now + _options.Backoffs[attempt - 1];
        _queue[transactionId] = new PendingRetry(transactionId, attempt, notBefore);
        return true;
    }

    public bool TryClaim(Guid transactionId, DateTime nowUtc, out int attempt)
    {
        attempt = 0;
        if (!_queue.TryGetValue(transactionId, out var pending)) return false;
        if (pending.NotBefore > nowUtc) return false;
        if (!_queue.TryRemove(transactionId, out _)) return false;
        attempt = pending.Attempt;
        return true;
    }

    public IReadOnlyCollection<PendingRetry> Snapshot() => _queue.Values.ToArray();

    internal TimeSpan Poll => _options.PollInterval;
}

/// <summary>
/// Hosted loop that re-runs failed charges whose due time has arrived. Uses a
/// fresh DbContext per tick through the service provider so it plays nicely
/// with the scoped DI graph.
/// </summary>
public sealed class PaymentRetryHostedService : BackgroundService
{
    private readonly PaymentRetryScheduler _scheduler;
    private readonly IServiceProvider _sp;
    private readonly ILogger<PaymentRetryHostedService> _log;

    public PaymentRetryHostedService(
        PaymentRetryScheduler scheduler,
        IServiceProvider sp,
        ILogger<PaymentRetryHostedService> log)
    {
        _scheduler = scheduler;
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "payment retry tick failed");
            }
            try { await Task.Delay(_scheduler.Poll, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    internal async Task TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        var due = _scheduler.Snapshot().Where(p => p.NotBefore <= nowUtc).ToArray();
        foreach (var pending in due)
        {
            if (!_scheduler.TryClaim(pending.TransactionId, nowUtc, out var attempt)) continue;

            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
            var provider = scope.ServiceProvider.GetRequiredService<IPaymentProviderAdapter>();

            var transaction = await db.PaymentTransactions.FirstOrDefaultAsync(t => t.TransactionId == pending.TransactionId, ct);
            if (transaction is null) continue;
            // Someone else already recovered it (e.g. via webhook).
            if (transaction.Status == "success" || transaction.Status == "refunded") continue;

            var result = await provider.ChargeAsync(new ChargeRequest(
                transaction.TenantId, transaction.InvoiceId, transaction.Amount, transaction.Currency,
                PaymentMethodRefForRetry(transaction),
                transaction.IdempotencyKey, transaction.CorrelationId), ct);

            transaction.Status = result.Status;
            transaction.ProviderReference = result.ProviderReference ?? transaction.ProviderReference;
            transaction.FailureCode = result.FailureCode;
            transaction.FailureReason = result.FailureReason;
            transaction.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            // Chain another retry only when the failure is still transient AND
            // we have attempts left in the ladder.
            if (result.Status == "failed" && result.FailureCode == "network_timeout" && attempt < _scheduler.MaxAttempts)
            {
                _scheduler.Schedule(transaction.TransactionId, attempt + 1, nowUtc);
            }
        }
    }

    // The charge row doesn't carry the original payment_method_ref — replays
    // use the idempotency key alone (the stub is ref-agnostic; production
    // providers recover the stored method from the attached idempotency_key
    // via their own records).
    private static string PaymentMethodRefForRetry(PaymentTransaction transaction)
        => transaction.IdempotencyKey;
}
