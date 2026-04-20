using System;

namespace Muallimi.Domain.Identity.ValueObjects;

/// <summary>
/// Opaque wrapper around a password hash string. Construction and
/// verification logic lives in <c>IPasswordService</c> (Application layer)
/// so the domain never depends on a specific hash algorithm.
/// </summary>
public sealed class PasswordHash : IEquatable<PasswordHash>
{
    public string Value { get; }

    public PasswordHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Password hash cannot be empty.", nameof(value));
        }
        if (value.Length > 500)
        {
            throw new ArgumentException("Password hash exceeds 500 characters.", nameof(value));
        }
        Value = value;
    }

    public override string ToString() => "[password-hash-redacted]";

    public bool Equals(PasswordHash? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PasswordHash other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
}
