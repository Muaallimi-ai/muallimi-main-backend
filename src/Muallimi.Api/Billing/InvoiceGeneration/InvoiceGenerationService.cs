using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.BlobStorage;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Billing.InvoiceGeneration;

/// <summary>
/// T035 + T040 — Invoice generation + PDF rendering. The local implementation
/// renders a deterministic text-based PDF surrogate (UTF-8 "%PDF" header +
/// structured body). A production provider-backed renderer plugs in later.
///
/// Arabic EGP amounts use Arabic-Indic numerals; diacritics in plan names
/// survive round-trip because the renderer writes UTF-8 bytes verbatim.
/// </summary>
public sealed record InvoiceLineItem(string DescriptionAr, string DescriptionEn, decimal Amount, string Currency);

public sealed record InvoiceCreateInput(
    Guid SubscriptionId,
    Guid TenantId,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    IReadOnlyList<InvoiceLineItem> LineItems,
    string Currency,
    decimal TaxAmount);

public interface IInvoiceGenerationService
{
    Task<Invoice> CreateAsync(InvoiceCreateInput input, CancellationToken ct = default);
    Task<(byte[] Pdf, string FileName)?> RenderPdfAsync(Guid invoiceId, string locale, CancellationToken ct = default);
    Task<IReadOnlyList<Invoice>> ListForTenantAsync(Guid tenantId, int limit, DateTime? beforeIssuedAt, CancellationToken ct = default);
    Task MarkPaidAsync(Guid invoiceId, CancellationToken ct = default);
    Task MarkFailedAsync(Guid invoiceId, CancellationToken ct = default);
    Task MarkRefundedAsync(Guid invoiceId, CancellationToken ct = default);
}

public sealed class InvoiceGenerationService : IInvoiceGenerationService
{
    private static readonly string[] ArabicIndicDigits = ["٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩"];

    private readonly MuallimiDbContext _db;
    private readonly ICurriculumBlobStore _blobs;

    public InvoiceGenerationService(MuallimiDbContext db, ICurriculumBlobStore blobs)
    {
        _db = db;
        _blobs = blobs;
    }

    public async Task<Invoice> CreateAsync(InvoiceCreateInput input, CancellationToken ct = default)
    {
        var subtotal = input.LineItems.Sum(li => li.Amount);
        var total = subtotal + input.TaxAmount;
        var now = DateTime.UtcNow;
        var invoiceNumber = $"INV-{now:yyyyMM}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

        var invoice = new Invoice
        {
            InvoiceId = Guid.NewGuid(),
            SubscriptionId = input.SubscriptionId,
            TenantId = input.TenantId,
            InvoiceNumber = invoiceNumber,
            PeriodStart = input.PeriodStart,
            PeriodEnd = input.PeriodEnd,
            LineItems = JsonSerializer.Serialize(input.LineItems),
            Subtotal = subtotal,
            TaxAmount = input.TaxAmount,
            Total = total,
            Currency = input.Currency,
            PaymentStatus = "pending",
            IssuedAt = now,
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<(byte[] Pdf, string FileName)?> RenderPdfAsync(Guid invoiceId, string locale, CancellationToken ct = default)
    {
        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.InvoiceId == invoiceId, ct);
        if (invoice is null) return null;

        var lines = JsonSerializer.Deserialize<List<InvoiceLineItem>>(invoice.LineItems) ?? [];
        var isArabic = locale == "ar";

        var body = new StringBuilder();
        body.AppendLine("%PDF-1.4  (Muaallimi local invoice surrogate)");
        body.AppendLine();
        body.AppendLine(isArabic ? "معلمي — فاتورة اشتراك" : "Muaallimi — Subscription Invoice");
        body.AppendLine($"{(isArabic ? "رقم الفاتورة" : "Invoice #")}: {invoice.InvoiceNumber}");
        body.AppendLine($"{(isArabic ? "الفترة" : "Period")}: {Fmt(invoice.PeriodStart, isArabic)} → {Fmt(invoice.PeriodEnd, isArabic)}");
        body.AppendLine();
        body.AppendLine(isArabic ? "البنود:" : "Line items:");
        foreach (var li in lines)
        {
            var desc = isArabic ? li.DescriptionAr : li.DescriptionEn;
            body.AppendLine($"  - {desc}  {FormatAmount(li.Amount, li.Currency, isArabic)}");
        }
        body.AppendLine();
        body.AppendLine($"{(isArabic ? "الإجمالي الفرعي" : "Subtotal")}: {FormatAmount(invoice.Subtotal, invoice.Currency, isArabic)}");
        body.AppendLine($"{(isArabic ? "الضريبة" : "Tax")}: {FormatAmount(invoice.TaxAmount, invoice.Currency, isArabic)}");
        body.AppendLine($"{(isArabic ? "الإجمالي" : "Total")}: {FormatAmount(invoice.Total, invoice.Currency, isArabic)}");

        var bytes = Encoding.UTF8.GetBytes(body.ToString());

        // Persist PDF blob key (best-effort — blob store writes are non-critical for invoice retrieval)
        var blobKey = $"invoices/{invoice.TenantId:N}/{invoice.InvoiceId:N}.{locale}.pdf";
        try
        {
            using var stream = new MemoryStream(bytes);
            await _blobs.UploadAsync(blobKey, stream, "application/pdf", ct);
            var tracked = await _db.Invoices.FirstAsync(i => i.InvoiceId == invoiceId, ct);
            if (string.IsNullOrEmpty(tracked.PdfBlobKey))
            {
                tracked.PdfBlobKey = blobKey;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch
        {
            // Blob upload failure is non-fatal — caller still receives the bytes.
        }

        return (bytes, $"{invoice.InvoiceNumber}.pdf");
    }

    public Task<IReadOnlyList<Invoice>> ListForTenantAsync(Guid tenantId, int limit, DateTime? beforeIssuedAt, CancellationToken ct = default)
    {
        var q = _db.Invoices.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (beforeIssuedAt is not null) q = q.Where(i => i.IssuedAt < beforeIssuedAt);
        return q.OrderByDescending(i => i.IssuedAt).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct)
            .ContinueWith<IReadOnlyList<Invoice>>(t => t.Result, ct);
    }

    public async Task MarkPaidAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == invoiceId, ct);
        if (invoice is null) return;
        invoice.PaymentStatus = "paid";
        invoice.PaidAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkFailedAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == invoiceId, ct);
        if (invoice is null) return;
        invoice.PaymentStatus = "failed";
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkRefundedAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == invoiceId, ct);
        if (invoice is null) return;
        invoice.PaymentStatus = "refunded";
        await _db.SaveChangesAsync(ct);
    }

    private static string FormatAmount(decimal amount, string currency, bool arabic)
    {
        var code = currency.ToUpperInvariant();
        var culture = arabic ? CultureInfo.GetCultureInfo("ar-EG") : CultureInfo.GetCultureInfo("en-US");
        var formatted = amount.ToString("N2", culture) + " " + code;
        return arabic ? ToArabicIndic(formatted) : formatted;
    }

    private static string Fmt(DateTime dt, bool arabic)
        => arabic ? ToArabicIndic(dt.ToString("yyyy-MM-dd")) : dt.ToString("yyyy-MM-dd");

    private static string ToArabicIndic(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is >= '0' and <= '9') sb.Append(ArabicIndicDigits[c - '0']);
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
