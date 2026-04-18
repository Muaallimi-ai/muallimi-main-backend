using System.Security.Cryptography;
using System.Text;

namespace Muallimi.Api.Payments.WebhookProcessing;

/// <summary>
/// T110 — Webhook signature validation abstraction. Each provider supplies its
/// own validator: the local stub uses HMAC-SHA256 over the raw body with a
/// shared secret; production providers (e.g. Stripe, Paymob) implement their
/// scheme's timestamp-scoped signature format. The registry picks the
/// validator by provider name; a missing match hard-rejects the webhook.
/// </summary>
public interface IWebhookSignatureValidator
{
    string ProviderName { get; }
    bool Validate(string body, IDictionary<string, string> headers, string secret);
}

/// <summary>
/// HMAC-SHA256(body, secret) compared in constant time against the
/// X-Provider-Signature header. Used by LocalPaymentStub and any custom
/// provider that adopts the same envelope.
/// </summary>
public sealed class LocalStubHmacSignatureValidator : IWebhookSignatureValidator
{
    public string ProviderName => "local_stub";

    public bool Validate(string body, IDictionary<string, string> headers, string secret)
    {
        if (!headers.TryGetValue("X-Provider-Signature", out var signatureHex) || string.IsNullOrEmpty(signatureHex))
            return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signatureHex.ToLowerInvariant()));
    }
}

/// <summary>
/// Stripe-style `Stripe-Signature: t=<timestamp>,v1=<hex>` validator. Verifies
/// HMAC-SHA256 over `timestamp.body` with the endpoint secret, inside a 5-minute
/// skew window. Production binding registers this under a specific provider name.
/// Kept here alongside the stub validator so adding a provider is a DI swap.
/// </summary>
public sealed class TimestampedHmacSignatureValidator : IWebhookSignatureValidator
{
    private readonly string _providerName;
    private readonly string _headerName;
    private readonly int _toleranceSeconds;

    public TimestampedHmacSignatureValidator(string providerName, string headerName, int toleranceSeconds = 300)
    {
        _providerName = providerName;
        _headerName = headerName;
        _toleranceSeconds = toleranceSeconds;
    }

    public string ProviderName => _providerName;

    public bool Validate(string body, IDictionary<string, string> headers, string secret)
    {
        if (!headers.TryGetValue(_headerName, out var header) || string.IsNullOrEmpty(header))
            return false;

        string? ts = null;
        string? v1 = null;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = part[..eq];
            var v = part[(eq + 1)..];
            if (k == "t") ts = v;
            else if (k == "v1") v1 = v;
        }
        if (ts is null || v1 is null) return false;
        if (!long.TryParse(ts, out var timestamp)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > _toleranceSeconds) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signed = ts + "." + body;
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(v1.ToLowerInvariant()));
    }
}

/// <summary>
/// Resolves the correct validator for an incoming provider webhook. Registered
/// as a singleton; validators are registered alongside IPaymentProviderAdapter
/// in Program.cs.
/// </summary>
public sealed class WebhookSignatureValidatorRegistry
{
    private readonly IReadOnlyDictionary<string, IWebhookSignatureValidator> _validators;

    public WebhookSignatureValidatorRegistry(IEnumerable<IWebhookSignatureValidator> validators)
    {
        _validators = validators.ToDictionary(v => v.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    public IWebhookSignatureValidator? ResolveFor(string providerName)
        => _validators.TryGetValue(providerName, out var v) ? v : null;
}
