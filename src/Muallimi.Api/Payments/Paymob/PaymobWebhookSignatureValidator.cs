using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Muallimi.Api.Payments.WebhookProcessing;

namespace Muallimi.Api.Payments.Paymob;

/// <summary>
/// Validates Paymob webhook signatures (HMAC-SHA512).
///
/// Paymob computes the HMAC by concatenating the string values of the following
/// transaction fields in this exact order, then running HMAC-SHA512 over the
/// result with the HMAC secret as the key. The computed hex string is sent in
/// the top-level "hmac" field of the JSON body.
///
/// Field order (official Paymob docs):
///   amount_cents, created_at, currency, error_occured, has_parent_transaction,
///   id, integration_id, is_3d_secure, is_auth, is_capture, is_refunded,
///   is_standalone_payment, is_voided, order.id, owner, pending,
///   source_data.pan, source_data.sub_type, source_data.type, success
/// </summary>
public sealed class PaymobWebhookSignatureValidator : IWebhookSignatureValidator
{
    public string ProviderName => "paymob";

    private static readonly string[] FieldOrder =
    [
        "amount_cents", "created_at", "currency", "error_occured",
        "has_parent_transaction", "id", "integration_id", "is_3d_secure",
        "is_auth", "is_capture", "is_refunded", "is_standalone_payment",
        "is_voided", "order.id", "owner", "pending",
        "source_data.pan", "source_data.sub_type", "source_data.type", "success",
    ];

    public bool Validate(string body, IDictionary<string, string> headers, string secret)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;

            // HMAC is sent either in the JSON body (older Paymob API)
            // or as a query-string parameter ?hmac=... (newer paymobsolutions.com API).
            string? receivedHmac = null;
            if (root.TryGetProperty("hmac", out var hmacEl))
                receivedHmac = hmacEl.GetString();

            if (string.IsNullOrEmpty(receivedHmac))
                headers.TryGetValue("query:hmac", out receivedHmac);

            if (string.IsNullOrEmpty(receivedHmac))
                return false;

            if (!root.TryGetProperty("obj", out var obj))
                return false;

            var sb = new StringBuilder();
            foreach (var field in FieldOrder)
            {
                sb.Append(ExtractField(obj, field));
            }

            var computed = ComputeHmacSha512(secret, sb.ToString());
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(receivedHmac.ToLowerInvariant()));
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractField(JsonElement obj, string path)
    {
        // Supports one level of nesting: "order.id", "source_data.pan"
        var parts = path.Split('.', 2);
        if (parts.Length == 2)
        {
            if (!obj.TryGetProperty(parts[0], out var nested)) return string.Empty;
            if (nested.ValueKind == JsonValueKind.Null) return string.Empty;
            return GetStringValue(nested, parts[1]);
        }

        return GetStringValue(obj, path);
    }

    private static string GetStringValue(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var val)) return string.Empty;
        return val.ValueKind switch
        {
            JsonValueKind.True    => "true",
            JsonValueKind.False   => "false",
            JsonValueKind.Null    => string.Empty,
            JsonValueKind.Number  => val.GetRawText(),
            JsonValueKind.String  => val.GetString() ?? string.Empty,
            _                     => val.GetRawText(),
        };
    }

    private static string ComputeHmacSha512(string secret, string message)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
