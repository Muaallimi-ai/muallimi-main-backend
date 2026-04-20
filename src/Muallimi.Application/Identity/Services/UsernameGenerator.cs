using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T087 — Generates the <c>firstname.birthyear.NNN</c> usernames for
/// Managed student accounts created under US2. The Arabic → Latin
/// transliteration is a minimal phonetic map (not a full Buckwalter /
/// UNGEGN romanization) — good enough for usernames and deterministic
/// so parents see the same style every time. The collision-retry loop
/// delegates the uniqueness check to the caller via
/// <see cref="IsUsernameTakenAsync"/> so this service stays DB-agnostic.
/// </summary>
public interface IUsernameGenerator
{
    /// <summary>
    /// Build a unique username for the given child. If
    /// <paramref name="preferred"/> is provided and free, it is used as-is
    /// (lower-cased, validated by the caller). Otherwise the service
    /// derives <c>{firstname}.{birthYear}.NNN</c> where <c>NNN</c> starts
    /// at a random 3-digit number and probes upward until a free slot is
    /// found (bounded by <see cref="MaxAttempts"/>).
    /// </summary>
    Task<string> GenerateAsync(
        string fullName,
        int birthYear,
        string? preferred,
        Func<string, CancellationToken, Task<bool>> isUsernameTakenAsync,
        CancellationToken ct = default);
}

public sealed class UsernameGenerator : IUsernameGenerator
{
    public const int MaxAttempts = 25;

    // Phonetic transliteration for the common Arabic letters used in
    // first names. Kept small on purpose — names with glottal stops,
    // ta marbuta, or unusual consonants fall back to "student".
    private static readonly IReadOnlyDictionary<char, string> ArabicToLatin = new Dictionary<char, string>
    {
        ['ا'] = "a", ['أ'] = "a", ['إ'] = "e", ['آ'] = "aa", ['ء'] = "",
        ['ب'] = "b", ['ت'] = "t", ['ث'] = "th", ['ج'] = "j",
        ['ح'] = "h", ['خ'] = "kh", ['د'] = "d", ['ذ'] = "dh",
        ['ر'] = "r", ['ز'] = "z", ['س'] = "s", ['ش'] = "sh",
        ['ص'] = "s", ['ض'] = "d", ['ط'] = "t", ['ظ'] = "z",
        ['ع'] = "a", ['غ'] = "gh", ['ف'] = "f", ['ق'] = "q",
        ['ك'] = "k", ['ل'] = "l", ['م'] = "m", ['ن'] = "n",
        ['ه'] = "h", ['و'] = "w", ['ي'] = "y", ['ى'] = "a",
        ['ة'] = "h", ['ؤ'] = "o", ['ئ'] = "e",
    };

    private static readonly Regex SafeUsername = new("[^a-z0-9.-]", RegexOptions.Compiled);

    private readonly Random _random;

    public UsernameGenerator() : this(new Random()) { }

    // Seeded constructor for deterministic test runs.
    public UsernameGenerator(Random random) { _random = random; }

    public async Task<string> GenerateAsync(
        string fullName,
        int birthYear,
        string? preferred,
        Func<string, CancellationToken, Task<bool>> isUsernameTakenAsync,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var normalized = preferred.Trim().ToLowerInvariant();
            if (!await isUsernameTakenAsync(normalized, ct).ConfigureAwait(false))
            {
                return normalized;
            }
            throw new InvalidOperationException("username_unavailable");
        }

        var first = ExtractFirstName(fullName);
        var latin = Transliterate(first);
        if (string.IsNullOrWhiteSpace(latin))
        {
            latin = "student";
        }

        for (var i = 0; i < MaxAttempts; i++)
        {
            var suffix = _random.Next(100, 1000); // 3-digit
            var candidate = $"{latin}.{birthYear:0000}.{suffix:000}";
            if (!await isUsernameTakenAsync(candidate, ct).ConfigureAwait(false))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("username_unavailable");
    }

    internal static string ExtractFirstName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;
        var trimmed = fullName.Trim();
        var first = trimmed.Split(new[] { ' ', '\t', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        return first;
    }

    internal static string Transliterate(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        // Strip Arabic diacritics first.
        var normalized = new string(input.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        foreach (var ch in normalized)
        {
            if (ArabicToLatin.TryGetValue(ch, out var mapped))
            {
                sb.Append(mapped);
            }
            else if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
            }
            else if (ch is >= 'A' and <= 'Z')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            // Other characters (spaces, punctuation, unknown letters) dropped.
        }
        var raw = sb.ToString();
        return SafeUsername.Replace(raw, "").Trim('.', '-');
    }
}
