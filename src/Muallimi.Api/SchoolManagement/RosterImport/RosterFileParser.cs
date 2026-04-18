using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.SchoolManagement.RosterImport;

/// <summary>
/// T054 (US2) — Roster CSV / spreadsheet parser.
///
/// Accepts UTF-8 comma-separated (and tab-separated) files with header rows
/// in either Arabic or English. The parser maps known Arabic headers to
/// canonical English field names before emitting a
/// <see cref="ParsedRosterRow"/> per data row. Quoted values with commas
/// (e.g. <c>"عبد الله, محمد"</c>) are respected; blank rows are ignored.
///
/// Arabic names — including diacritics — are preserved with full fidelity;
/// normalisation is the validator's job, not the parser's.
/// </summary>
public sealed record ParsedRosterRow(
    int RowNumber,
    string StudentNameAr,
    string StudentNameEn,
    string Grade,
    string ParentName,
    string ParentEmail,
    string? ParentPhone,
    string? ClassSection,
    string? StudentNationalId);

public sealed record RosterParseResult(
    IReadOnlyList<ParsedRosterRow> Rows,
    IReadOnlyList<string> HeaderErrors);

public interface IRosterFileParser
{
    RosterParseResult Parse(Stream stream, string fileName);
}

public sealed class RosterFileParser : IRosterFileParser
{
    private static readonly IReadOnlyDictionary<string, string> HeaderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Arabic headers → canonical.
            ["اسم الطالب بالعربية"] = "student_name_ar",
            ["الاسم بالعربية"] = "student_name_ar",
            ["اسم الطالب"] = "student_name_ar",
            ["اسم الطالب بالإنجليزية"] = "student_name_en",
            ["الاسم بالإنجليزية"] = "student_name_en",
            ["الصف"] = "grade",
            ["اسم ولي الأمر"] = "parent_name",
            ["بريد ولي الأمر"] = "parent_email",
            ["البريد الإلكتروني"] = "parent_email",
            ["هاتف ولي الأمر"] = "parent_phone",
            ["شعبة الصف"] = "class_section",
            ["الشعبة"] = "class_section",
            ["الهوية الوطنية"] = "student_national_id",
            ["رقم الهوية"] = "student_national_id",

            // English canonical (already canonical — kept here for
            // mixed-case / whitespace tolerance).
            ["student_name_ar"] = "student_name_ar",
            ["student_name_en"] = "student_name_en",
            ["grade"] = "grade",
            ["parent_name"] = "parent_name",
            ["parent_email"] = "parent_email",
            ["parent_phone"] = "parent_phone",
            ["class_section"] = "class_section",
            ["student_national_id"] = "student_national_id",
        };

    private static readonly string[] RequiredColumns =
    {
        "student_name_ar",
        "student_name_en",
        "grade",
        "parent_name",
        "parent_email",
    };

    public RosterParseResult Parse(Stream stream, string fileName)
    {
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var raw = reader.ReadToEnd();
        var delimiter = DetectDelimiter(raw, fileName);
        var lines = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            return new RosterParseResult(Array.Empty<ParsedRosterRow>(), new[] { "header_row_missing" });

        var headers = SplitLine(lines[0], delimiter)
            .Select(h => h.Trim().Trim('\uFEFF'))
            .Select(h => HeaderAliases.TryGetValue(h, out var canonical) ? canonical : h)
            .ToList();

        var headerErrors = new List<string>();
        foreach (var required in RequiredColumns)
        {
            if (!headers.Contains(required)) headerErrors.Add($"missing_column:{required}");
        }
        if (headerErrors.Count > 0)
            return new RosterParseResult(Array.Empty<ParsedRosterRow>(), headerErrors);

        var rows = new List<ParsedRosterRow>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cells = SplitLine(line, delimiter);
            string? Cell(string key)
            {
                var idx = headers.IndexOf(key);
                if (idx < 0 || idx >= cells.Count) return null;
                var value = cells[idx]?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }

            rows.Add(new ParsedRosterRow(
                RowNumber: i + 1,
                StudentNameAr: Cell("student_name_ar") ?? string.Empty,
                StudentNameEn: Cell("student_name_en") ?? string.Empty,
                Grade: Cell("grade") ?? string.Empty,
                ParentName: Cell("parent_name") ?? string.Empty,
                ParentEmail: Cell("parent_email") ?? string.Empty,
                ParentPhone: Cell("parent_phone"),
                ClassSection: Cell("class_section"),
                StudentNationalId: Cell("student_national_id")));
        }

        return new RosterParseResult(rows, Array.Empty<string>());
    }

    private static char DetectDelimiter(string content, string fileName)
    {
        if (fileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase)) return '\t';
        // Count tabs vs commas on the first line to cope with tab-delimited
        // exports from Excel that keep the .csv extension.
        var firstNewline = content.IndexOfAny(new[] { '\n', '\r' });
        var headerLine = firstNewline < 0 ? content : content.Substring(0, firstNewline);
        return headerLine.Count(c => c == '\t') > headerLine.Count(c => c == ',') ? '\t' : ',';
    }

    private static List<string> SplitLine(string line, char delimiter)
    {
        var result = new List<string>();
        var buffer = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (ch == delimiter && !inQuotes)
            {
                result.Add(buffer.ToString());
                buffer.Clear();
                continue;
            }
            buffer.Append(ch);
        }
        result.Add(buffer.ToString());
        return result;
    }
}

public static class RosterFileParserServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5RosterFileParser(this IServiceCollection services)
    {
        services.AddSingleton<IRosterFileParser, RosterFileParser>();
        return services;
    }
}
