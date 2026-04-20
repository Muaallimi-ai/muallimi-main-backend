using System;
using System.Text.RegularExpressions;

namespace Muallimi.Domain.Identity.ValueObjects;

/// <summary>
/// Email value object. Stores the canonical form (<see cref="Value"/>) and
/// the normalized form (<see cref="Normalized"/> — uppercase, used by the
/// unique index). Validation is RFC-5322-lite: a practical subset that
/// accepts the shapes real users enter and rejects obvious malformed
/// input without pulling in the full RFC grammar.
/// </summary>
public sealed class Email : IEquatable<Email>
{
    // Practical RFC-5322 subset — same shape as widely-used validators.
    private static readonly Regex Pattern = new(
        @"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Value { get; }
    public string Normalized { get; }

    private Email(string value, string normalized)
    {
        Value = value;
        Normalized = normalized;
    }

    public static Email Parse(string raw)
    {
        if (!TryParse(raw, out var email))
        {
            throw new ArgumentException("Invalid email address.", nameof(raw));
        }
        return email!;
    }

    public static bool TryParse(string? raw, out Email? email)
    {
        email = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        if (trimmed.Length > 255) return false;
        if (!Pattern.IsMatch(trimmed)) return false;
        email = new Email(trimmed, trimmed.ToUpperInvariant());
        return true;
    }

    public override string ToString() => Value;

    public bool Equals(Email? other) =>
        other is not null && string.Equals(Normalized, other.Normalized, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Email other && Equals(other);

    public override int GetHashCode() => Normalized.GetHashCode(StringComparison.Ordinal);
}
