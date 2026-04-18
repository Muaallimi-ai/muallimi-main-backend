using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Billing.InvoiceGeneration;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.DownstreamEvents;
using Muallimi.Api.Payments.PaymentProviderAdapter;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Payments.RefundProcessing;

/// <summary>
/// T042 — Full + partial refunds through PaymentProviderAdapter. Records a new
/// refund PaymentTransaction row linked to the original charge and updates
/// invoice status to refunded on success.
/// </summary>
public sealed record RefundCommand(
    Guid TransactionId,
    decimal Amount,
    string Reason,
    string IdempotencyKey,
    string CorrelationId);

public sealed record RefundOutcome(Guid RefundTransactionId, string Status, string? ProviderReference, string? FailureReason);

public interface IRefundService
{
    Task<RefundOutcome?> RefundAsync(RefundCommand cmd, CancellationToken ct = default);
}

public sealed class RefundService : IRefundService
{
    private readonly MuallimiDbContext _db;
    private readonly IPaymentProviderAdapter _provider;
    private readonly IInvoiceGenerationService _invoices;
    private readonly Phase6OperationalEventOutbox _outbox;
    private readonly AuditTrailWriter _audit;

    public RefundService(
        MuallimiDbContext db,
        IPaymentProviderAdapter provider,
        IInvoiceGenerationService invoices,
        Phase6OperationalEventOutbox outbox,
        AuditTrailWriter audit)
    {
        _db = db;
        _provider = provider;
        _invoices = invoices;
        _outbox = outbox;
        _audit = audit;
    }

    public async Task<RefundOutcome?> RefundAsync(RefundCommand cmd, CancellationToken ct = default)
    {
        var original = await _db.PaymentTransactions.FirstOrDefaultAsync(t => t.TransactionId == cmd.TransactionId, ct);
        if (original is null || original.TransactionType != "charge" || original.Status != "success")
            return null;

        if (cmd.Amount <= 0 || cmd.Amount > original.Amount)
            throw new ArgumentOutOfRangeException(nameof(cmd), "Refund amount must be > 0 and <= original charge");

        var now = DateTime.UtcNow;
        var refund = new PaymentTransaction
        {
            TransactionId = Guid.NewGuid(),
            InvoiceId = original.InvoiceId,
            SubscriptionId = original.SubscriptionId,
            TenantId = original.TenantId,
            ProviderName = _provider.ProviderName,
            Amount = cmd.Amount,
            Currency = original.Currency,
            TransactionType = "refund",
            Status = "pending",
            IdempotencyKey = cmd.IdempotencyKey,
            CorrelationId = cmd.CorrelationId,
            AttemptedAt = now,
        };
        _db.PaymentTransactions.Add(refund);
        await _db.SaveChangesAsync(ct);

        var result = await _provider.RefundAsync(new RefundRequest(
            original.ProviderReference ?? original.TransactionId.ToString(),
            cmd.Amount, original.Currency, cmd.IdempotencyKey, cmd.CorrelationId), ct);

        refund.Status = result.Status == "success" ? "refunded" : result.Status;
        refund.ProviderReference = result.ProviderReference;
        refund.FailureReason = result.FailureReason;
        refund.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (result.Status == "success")
        {
            await _invoices.MarkRefundedAsync(original.InvoiceId, ct);
        }

        await _outbox.EnqueueAsync(original.TenantId, "payment_refunded", new
        {
            original_transaction_id = original.TransactionId,
            refund_transaction_id = refund.TransactionId,
            amount = cmd.Amount,
            currency = original.Currency,
            status = refund.Status,
        }, cmd.CorrelationId, ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = original.TenantId,
            ActorId = original.TenantId,
            ActorType = "tenant",
            TargetId = refund.TransactionId,
            TargetType = "payment_transaction",
            ActionType = "payment.refunded",
            Payload = new { original_transaction_id = original.TransactionId, amount = cmd.Amount, reason = cmd.Reason },
            CorrelationId = cmd.CorrelationId,
        }, ct);

        return new RefundOutcome(refund.TransactionId, refund.Status, refund.ProviderReference, refund.FailureReason);
    }
}
