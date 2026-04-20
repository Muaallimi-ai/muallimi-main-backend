using System;
using BCryptLib = BCrypt.Net.BCrypt;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T029 — Password hashing service. BCrypt with work factor 12.
/// <see cref="VerifyWithDummyFallback"/> runs a constant-time BCrypt
/// operation even when the user doesn't exist, so the login endpoint's
/// unknown-email path has the same latency as the known-email-wrong-password
/// path (SC-009 / T061 timing-attack invariance).
/// </summary>
public interface IPasswordService
{
    string Hash(string plaintext);
    bool Verify(string plaintext, string hash);
    /// <summary>
    /// Runs a BCrypt verification against either the real hash (when
    /// <paramref name="hash"/> is non-null) or a pre-computed dummy hash.
    /// Always returns <c>false</c> for the dummy path; the caller maps that
    /// back to "invalid credentials" without branching on user-existence.
    /// </summary>
    bool VerifyWithDummyFallback(string plaintext, string? hash);
}

public sealed class BCryptPasswordService : IPasswordService
{
    public const int WorkFactor = 12;

    // Pre-computed BCrypt hash of a fixed constant — same work factor as real
    // hashes so the compare takes the same wall-clock time. The plaintext
    // "dummy-password-for-timing-invariance" is never considered valid.
    private static readonly string DummyHash = BCryptLib.HashPassword(
        "dummy-password-for-timing-invariance", workFactor: WorkFactor);

    public string Hash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return BCryptLib.HashPassword(plaintext, workFactor: WorkFactor);
    }

    public bool Verify(string plaintext, string hash)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        ArgumentException.ThrowIfNullOrEmpty(hash);
        return BCryptLib.Verify(plaintext, hash);
    }

    public bool VerifyWithDummyFallback(string plaintext, string? hash)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            // Still run the dummy to equalize latency for empty-password attempts.
            _ = BCryptLib.Verify("", DummyHash);
            return false;
        }
        if (string.IsNullOrEmpty(hash))
        {
            _ = BCryptLib.Verify(plaintext, DummyHash);
            return false;
        }
        return BCryptLib.Verify(plaintext, hash);
    }
}
