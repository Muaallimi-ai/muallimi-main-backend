using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Application.Common;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T055 (US2) — per-row validator for <see cref="ParsedRosterRow"/>.
///
/// Enforces the contract's validation rules:
///   • required fields (name_ar, name_en, grade, parent_name, parent_email);
///   • parent_email looks like an email;
///   • grade parses as an integer inside the school's configured
///     [grade_range_start, grade_range_end];
///   • an in-file de-duplication key — (normalised name_ar, grade,
///     parent_email) — rejects the second occurrence as a skip (not an
///     error) so the import surfaces them separately;
///   • Arabic names keep their original diacritics and combining marks in
///     the emitted payload; normalisation is only used to compute the
///     duplicate key.
/// </summary>
public sealed record ValidatedRosterRow(
    ParsedRosterRow Source,
    int Grade,
    string DedupKey,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record RosterValidationResult(
    IReadOnlyList<ValidatedRosterRow> ValidRows,
    IReadOnlyList<ValidatedRosterRow> RejectedRows,
    IReadOnlyList<ValidatedRosterRow> SkippedDuplicates);

public interface IRosterRowValidator
{
    RosterValidationResult Validate(
        IReadOnlyList<ParsedRosterRow> parsedRows,
        int gradeRangeStart,
        int gradeRangeEnd);
}

public sealed class RosterRowValidator : IRosterRowValidator
{
    private static readonly Regex EmailRegex =
        new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.Compiled);

    public RosterValidationResult Validate(
        IReadOnlyList<ParsedRosterRow> parsedRows,
        int gradeRangeStart,
        int gradeRangeEnd)
    {
        var valid = new List<ValidatedRosterRow>();
        var rejected = new List<ValidatedRosterRow>();
        var duplicates = new List<ValidatedRosterRow>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in parsedRows)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(row.StudentNameAr)) errors.Add("missing:student_name_ar");
            if (string.IsNullOrWhiteSpace(row.StudentNameEn)) errors.Add("missing:student_name_en");
            if (string.IsNullOrWhiteSpace(row.ParentName)) errors.Add("missing:parent_name");
            if (string.IsNullOrWhiteSpace(row.ParentEmail)) errors.Add("missing:parent_email");
            else if (!EmailRegex.IsMatch(row.ParentEmail)) errors.Add("invalid:parent_email");

            var grade = 0;
            if (string.IsNullOrWhiteSpace(row.Grade))
            {
                errors.Add("missing:grade");
            }
            else
            {
                if (!int.TryParse(
                        row.Grade,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out grade))
                {
                    errors.Add("invalid:grade_not_integer");
                }
                else if (grade < gradeRangeStart || grade > gradeRangeEnd)
                {
                    errors.Add($"out_of_range:grade={grade}");
                }
            }

            var dedupKey = ComputeDedupKey(row, grade);
            var validated = new ValidatedRosterRow(row, grade, dedupKey, errors);

            if (errors.Count > 0)
            {
                rejected.Add(validated);
                continue;
            }

            if (!seenKeys.Add(dedupKey))
            {
                duplicates.Add(validated);
                continue;
            }

            valid.Add(validated);
        }

        return new RosterValidationResult(valid, rejected, duplicates);
    }

    /// <summary>
    /// Deterministic key used to collapse in-file duplicates. Normalises
    /// Arabic variants (alif forms, ya vs alif maqsura, ta marbuta vs ha,
    /// tatweel, diacritics) and folds English to lower-case. The emitted
    /// payload keeps the original diacritics; only this key is normalised.
    /// </summary>
    public static string ComputeDedupKey(ParsedRosterRow row, int grade)
    {
        // Reuse the platform's shared Arabic-name normalizer so this dedup
        // key matches the parent-managed-child duplicate check character
        // for character. See `Muallimi.Application.Common.NameNormalization`.
        var nameKey = NameNormalization.NormalizeArabic(row.StudentNameAr);
        var email = (row.ParentEmail ?? string.Empty).Trim().ToLowerInvariant();
        return $"{nameKey}|{grade}|{email}";
    }
}

public static class RosterRowValidatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5RosterRowValidator(this IServiceCollection services)
    {
        services.AddSingleton<IRosterRowValidator, RosterRowValidator>();
        return services;
    }
}
