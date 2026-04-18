using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Muallimi.Infrastructure.Persistence;

/// <summary>
/// T090 — Column-level encryption plumbing. The concrete adapter lives in the
/// API layer (<c>Muallimi.Api.Security.DataEncryption</c>). Startup wires the
/// delegates here so <see cref="EncryptedStringConverter"/> and
/// <see cref="EncryptedJsonConverter"/> can transparently encrypt on write and
/// decrypt on materialisation. Null or already-ciphertext inputs short-circuit.
/// </summary>
public static class ColumnEncryption
{
    public const string CipherPrefix = "enc:v1:";

    public static Func<string, string> Encrypt { get; set; } = plaintext => plaintext;
    public static Func<string, string> Decrypt { get; set; } = ciphertext => ciphertext;

    public static string EncryptValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (value.StartsWith(CipherPrefix, StringComparison.Ordinal)) return value;
        return CipherPrefix + Encrypt(value);
    }

    public static string DecryptValue(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored ?? string.Empty;
        if (!stored.StartsWith(CipherPrefix, StringComparison.Ordinal)) return stored;
        return Decrypt(stored.Substring(CipherPrefix.Length));
    }
}

/// <summary>
/// Value converter for plain string columns that stores ciphertext with an
/// <c>enc:v1:</c> prefix. Pre-existing plaintext values round-trip unchanged,
/// so the converter is safe to retrofit onto an existing column without
/// rewriting historical rows.
/// </summary>
public class EncryptedStringConverter : ValueConverter<string?, string?>
{
    public EncryptedStringConverter()
        : base(
            v => v == null ? null : ColumnEncryption.EncryptValue(v),
            v => v == null ? null : ColumnEncryption.DecryptValue(v))
    {
    }
}

/// <summary>
/// Non-null variant for jsonb columns that are modelled as non-null strings
/// (e.g. <c>NotificationProviderBinding.Configuration</c>).
/// </summary>
public class EncryptedJsonConverterNonNull : ValueConverter<string, string>
{
    public EncryptedJsonConverterNonNull()
        : base(
            v => EncryptedJsonConverter.WrapPublic(v),
            v => EncryptedJsonConverter.UnwrapPublic(v))
    {
    }
}

/// <summary>
/// Value converter for jsonb columns. Wraps the ciphertext inside a JSON
/// envelope <c>{"ct":"&lt;prefix+base64&gt;"}</c> so PostgreSQL still accepts
/// the value as valid JSON. Legacy plaintext JSON is preserved on read.
/// </summary>
public class EncryptedJsonConverter : ValueConverter<string?, string?>
{
    public EncryptedJsonConverter()
        : base(
            v => Wrap(v),
            v => Unwrap(v))
    {
    }

    public static string WrapPublic(string? plaintext) => Wrap(plaintext) ?? string.Empty;
    public static string UnwrapPublic(string? stored) => Unwrap(stored) ?? string.Empty;

    private static string? Wrap(string? plaintext)
    {
        if (plaintext == null) return null;
        if (string.IsNullOrEmpty(plaintext)) return "{}";
        var cipher = ColumnEncryption.EncryptValue(plaintext);
        // Escape quotes for JSON safety.
        var escaped = cipher.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "{\"ct\":\"" + escaped + "\"}";
    }

    private static string? Unwrap(string? stored)
    {
        if (stored == null) return null;
        if (string.IsNullOrEmpty(stored)) return stored;
        // Detect the encrypted envelope shape.
        var trimmed = stored.TrimStart();
        if (trimmed.StartsWith("{\"ct\":\"", StringComparison.Ordinal))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(stored);
                if (doc.RootElement.TryGetProperty("ct", out var ct) && ct.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return ColumnEncryption.DecryptValue(ct.GetString());
                }
            }
            catch
            {
                return stored;
            }
        }
        return stored;
    }
}
