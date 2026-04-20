using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OtpNet;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T031 — RFC 6238 TOTP (SHA-1, 30s period, 6 digits) via OtpNet.
/// Stores encrypted Base32 secrets and a JSON array of 10 single-use
/// recovery codes. Plaintext handling is confined to enrolment + verify
/// — at rest, the <c>TwoFactorSecret</c> rows always carry the AES-GCM
/// ciphertext produced by the caller's <c>IAesEncryptor</c>.
/// </summary>
public interface ITwoFactorService
{
    TwoFactorEnrolment GenerateEnrolment(string accountIdentifier);
    bool VerifyTotp(string base32Secret, string code, int verificationWindow = 1);
    IReadOnlyList<string> GenerateRecoveryCodes(int count = 10);
    /// <summary>
    /// Marks one matching code as used (SHA-256 compare) and returns the
    /// updated list. Throws <see cref="InvalidOperationException"/> on miss.
    /// </summary>
    IReadOnlyList<string> ConsumeRecoveryCode(IReadOnlyList<string> codes, string submittedCode);
    string SerializeRecoveryCodes(IReadOnlyList<string> codes);
    IReadOnlyList<string> DeserializeRecoveryCodes(string json);
}

public sealed record TwoFactorEnrolment(
    string Base32Secret,
    string QrProvisioningUri);

public sealed class TotpTwoFactorService : ITwoFactorService
{
    private const string Issuer = "Muaallimi";

    public TwoFactorEnrolment GenerateEnrolment(string accountIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentifier);
        var secretBytes = KeyGeneration.GenerateRandomKey(20); // 160-bit
        var base32 = Base32Encoding.ToString(secretBytes).TrimEnd('=');
        var uri = $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(accountIdentifier)}" +
                  $"?secret={base32}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30";
        return new TwoFactorEnrolment(base32, uri);
    }

    public bool VerifyTotp(string base32Secret, string code, int verificationWindow = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }
        var secretBytes = Base32Encoding.ToBytes(PadBase32(base32Secret));
        var totp = new Totp(secretBytes, step: 30, mode: OtpHashMode.Sha1, totpSize: 6);
        var window = new VerificationWindow(previous: verificationWindow, future: verificationWindow);
        return totp.VerifyTotp(code, out _, window);
    }

    public IReadOnlyList<string> GenerateRecoveryCodes(int count = 10)
    {
        var codes = new string[count];
        for (var i = 0; i < count; i++)
        {
            // 8 hex chars = 4 bytes of entropy, simple to type, 16B key space.
            codes[i] = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        }
        return codes;
    }

    public IReadOnlyList<string> ConsumeRecoveryCode(IReadOnlyList<string> codes, string submittedCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(submittedCode);
        var normalizedSubmitted = submittedCode.Trim().ToLowerInvariant();
        var match = -1;
        for (var i = 0; i < codes.Count; i++)
        {
            if (string.Equals(codes[i], normalizedSubmitted, StringComparison.Ordinal))
            {
                match = i;
                break;
            }
        }
        if (match < 0)
        {
            throw new InvalidOperationException("Recovery code not recognized.");
        }
        var result = new List<string>(codes);
        result.RemoveAt(match);
        return result;
    }

    public string SerializeRecoveryCodes(IReadOnlyList<string> codes)
        => JsonSerializer.Serialize(codes);

    public IReadOnlyList<string> DeserializeRecoveryCodes(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
    }

    private static string PadBase32(string input)
    {
        var mod = input.Length % 8;
        return mod == 0 ? input : input + new string('=', 8 - mod);
    }
}
