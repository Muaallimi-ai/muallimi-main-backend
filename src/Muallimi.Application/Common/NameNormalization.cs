using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Muallimi.Application.Common;

/// <summary>
/// Cross-feature name normalization helper used by every "is this the same
/// person?" comparison across the platform — Phase 5 roster import dedup,
/// Phase 9 parent-managed-child duplicate detection, and any future code
/// that needs to compare two human-typed Arabic names for equality.
///
/// Folds every common Arabic typing variation onto a single canonical form
/// so "علي شامة", "علي شامه", "علي  شامة", and "ALI SHAMA" all collapse to
/// the same key. The original string is left untouched in the user-facing
/// row — only the comparison key flows through this helper.
///
/// Phase 5's <c>RosterRowValidator.NormaliseArabic</c> previously owned
/// this logic; the file now delegates here so the two paths can never
/// drift.
/// </summary>
public static class NameNormalization
{
    /// <summary>
    /// Normalize an Arabic / English / mixed name into a comparison key.
    /// Steps: strip diacritics (combining marks), fold alif variants
    /// (<c>أ إ آ ٱ → ا</c>), fold alif-maqsura to ya (<c>ى → ي</c>), fold
    /// ta-marbuta to ha (<c>ة → ه</c>), drop tatweel (<c>ـ</c>), collapse
    /// internal whitespace, and lowercase ASCII letters. Returns an empty
    /// string for null / blank input.
    /// </summary>
    public static string NormalizeArabic(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Strip diacritics (category Mn — combining marks), including the
        // Arabic harakat range.
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            stripped.Append(ch);
        }
        var flattened = stripped.ToString();

        var mapped = new StringBuilder(flattened.Length);
        foreach (var ch in flattened)
        {
            switch (ch)
            {
                case 'أ':
                case 'إ':
                case 'آ':
                case 'ٱ':
                    mapped.Append('ا');
                    break;
                case 'ى':
                    mapped.Append('ي');
                    break;
                case 'ة':
                    mapped.Append('ه');
                    break;
                case 'ـ':
                    break; // tatweel
                default:
                    mapped.Append(ch);
                    break;
            }
        }

        var collapsed = Regex.Replace(mapped.ToString(), @"\s+", " ").Trim();
        return collapsed.ToLowerInvariant();
    }
}
