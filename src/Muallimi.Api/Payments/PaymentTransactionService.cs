using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Billing;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Payments.PaymentProviderAdapter;
using Muallimi.Api.Payments.RetryPolicy;
using Muallimi.Api.Security.DataEncryption;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Payments;

/// <summary>
/// T036 — PaymentTransaction persistence + charge flow. Records every provider
/// attempt with idempotency-keyed deduplication and encrypted webhook payloads.
/// </summary>
public sealed record ChargeCommand(
    Guid InvoiceId,
    Guid SubscriptionId,
    Guid TenantId,
    decimal Amount,
    string Currency,
    string PaymentMethodRef,
    string IdempotencyKey,
    string CorrelationId);

public interface IPaymentTransactionService
{
    Task<PaymentTransaction> ChargeAsync(ChargeCommand cmd, CancellationToken ct = default);
    Task<PaymentTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);
    Task<PaymentTransaction?> RecordWebhookAsync(string providerReference, string webhookPayload, string eventType, CancellationToken ct = default);
}

public sealed class PaymentTransactionService : IPaymentTransactionService
{
    private readonly MuallimiDbContext _db;
    private readonly IPaymentProviderAdapter _provider;
    private readonly IDataEncryptionAdapter _encryption;
    private readonly Phase6OperationalEventOutbox _outbox;
    private readonly AuditTrailWriter _audit;
    private readonly IBillingNotificationDispatcher? _notifications;
    private readonly PaymentRetryScheduler? _retryScheduler;

    public PaymentTransactionService(
        MuallimiDbContext db,
        IPaymentProviderAdapter provider,
        IDataEncryptionAdapter encryption,
        Phase6OperationalEventOutbox outbox,
        AuditTrailWriter audit,
        IBillingNotificationDispatcher? notifications = null,
        PaymentRetryScheduler? retryScheduler = null)
    {
        _db = db;
        _provider = provider;
        _encryption = encryption;
        _outbox = outbox;
        _audit = audit;
        _notifications = notifications;
        _retryScheduler = retryScheduler;
    }

    public async Task<PaymentTransaction> ChargeAsync(ChargeCommand cmd, CancellationToken ct = default)
    {
        var existing = await _db.PaymentTransactions
            .FirstOrDefaultAsync(t => t.IdempotencyKey == cmd.IdempotencyKey, ct);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        var transaction = new PaymentTransaction
        {
            TransactionId = Guid.NewGuid(),
            InvoiceId = cmd.InvoiceId,
            SubscriptionId = cmd.SubscriptionId,
            TenantId = cmd.TenantId,
            ProviderName = _provider.ProviderName,
            Amount = cmd.Amount,
            Currency = cmd.Currency,
            TransactionType = "charge",
            Status = "pending",
            IdempotencyKey = cmd.IdempotencyKey,
            CorrelationId = cmd.CorrelationId,
            AttemptedAt = now,
        };
        _db.PaymentTransactions.Add(transaction);
        await _db.SaveChangesAsync(ct);

        var result = await _provider.ChargeAsync(new ChargeRequest(
            cmd.TenantId, cmd.InvoiceId, cmd.Amount, cmd.Currency, cmd.PaymentMethodRef, cmd.IdempotencyKey, cmd.CorrelationId), ct);

        transaction.Status = result.Status;
        transaction.ProviderReference = result.ProviderReference;
        transaction.FailureCode = result.FailureCode;
        transaction.FailureReason = result.FailureReason;
        transaction.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // T112 — Transient network_timeout failures enter the retry ladder (30s/5m/30m).
        // Permanent failures (insufficient_funds, expired_card, fraud_hold) skip the queue.
        if (result.Status == "failed" && result.FailureCode == "network_timeout")
        {
            _retryScheduler?.Schedule(transaction.TransactionId, attempt: 1);
        }

        var eventKind = result.Status == "success" ? "payment_processed" : "payment_failed";
        await _outbox.EnqueueAsync(cmd.TenantId, eventKind, new
        {
            transaction_id = transaction.TransactionId,
            invoice_id = cmd.InvoiceId,
            amount = cmd.Amount,
            currency = cmd.Currency,
            status = result.Status,
            failure_code = result.FailureCode,
        }, cmd.CorrelationId, ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = cmd.TenantId,
            ActorId = cmd.TenantId,
            ActorType = "tenant",
            TargetId = transaction.TransactionId,
            TargetType = "payment_transaction",
            ActionType = $"payment.{result.Status}",
            Payload = new { invoice_id = cmd.InvoiceId, amount = cmd.Amount, currency = cmd.Currency, failure_code = result.FailureCode },
            CorrelationId = cmd.CorrelationId,
        }, ct);

        // T061 — Notify the payer. Critical failures bypass quiet hours via
        // QuietHoursPolicy. Recipient = tenant for local parity (in production
        // this resolves to the billing contact on the tenant record).
        if (_notifications is not null)
        {
            var ctx = new BillingNotificationContext(
                TenantId: cmd.TenantId,
                RecipientId: cmd.TenantId,
                NotificationId: transaction.TransactionId,
                Language: "ar",
                CorrelationId: cmd.CorrelationId);
            if (result.Status == "success")
                await _notifications.DispatchPaymentSucceededAsync(ctx, ct);
            else if (result.Status == "failed")
                await _notifications.DispatchPaymentFailedAsync(ctx, result.FailureCode, ct);
        }

        return transaction;
    }

    public Task<PaymentTransaction?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        => _db.PaymentTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.IdempotencyKey == idempotencyKey, ct);

    public async Task<PaymentTransaction?> RecordWebhookAsync(string providerReference, string webhookPayload, string eventType, CancellationToken ct = default)
    {
        var transaction = await _db.PaymentTransactions
            .FirstOrDefaultAsync(t => t.ProviderReference == providerReference, ct);
        if (transaction is null) return null;

        transaction.WebhookPayload = _encryption.Encrypt(webhookPayload);
        if (eventType == "payment_succeeded") transaction.Status = "success";
        else if (eventType == "payment_failed") transaction.Status = "failed";
        else if (eventType == "refund_completed") transaction.Status = "refunded";
        transaction.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return transaction;
    }
}
