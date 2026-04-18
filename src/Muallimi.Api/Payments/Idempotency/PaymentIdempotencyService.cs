using Microsoft.EntityFrameworkCore;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Payments.Idempotency;

/// <summary>
/// T111 — Payment idempotency with a 24-hour dedup window.
///
/// Deduplication key = (provider_reference, transaction_type). If a completed
/// transaction matching both fields is already present within the last 24 hours
/// the incoming webhook / replay is treated as a duplicate and the new state
/// change is skipped. Outside the window the dedup expires so genuinely new
/// transactions that reuse a reference are not blocked indefinitely.
///
/// Dedup is read-only against PaymentTransactions — persistence happens through
/// PaymentTransactionService.RecordWebhookAsync. This keeps the whole flow in
/// one table without a separate dedup log.
/// </summary>
public sealed class PaymentIdempotencyService
{
    public static readonly TimeSpan DedupWindow = TimeSpan.FromHours(24);

    private readonly MuallimiDbContext _db;

    public PaymentIdempotencyService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsDuplicateWebhookAsync(
        string providerReference,
        string transactionType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(providerReference) || string.IsNullOrEmpty(transactionType)) return false;

        var cutoff = DateTime.UtcNow - DedupWindow;
        return await _db.PaymentTransactions.AsNoTracking().AnyAsync(
            t => t.ProviderReference == providerReference
              && t.TransactionType == transactionType
              && t.CompletedAt != null
              && t.CompletedAt >= cutoff,
            ct);
    }
}
