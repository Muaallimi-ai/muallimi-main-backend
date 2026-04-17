using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.StudentExperience.HomeworkHelp;

/// <summary>
/// T105 (US7) — OCR adapter abstraction for homework image submissions.
///
/// The Phase 3 facade never runs OCR locally; it forwards the image bytes
/// (already EXIF-stripped + resized client-side) to a Phase 2 OCR/vision
/// adapter binding. Local-mode swaps in <see cref="LocalEchoOcrAdapter"/>
/// so e2e + integration tests can exercise the contract without an external
/// service. Production replaces the binding via DI.
///
/// The result carries the extracted text, the resolved adapter binding id
/// (so the persisted submission can cite it for audit), and a confidence
/// score. <see cref="OcrOutcome.Unreadable"/> is treated as a refusal at
/// the service layer (refusal_reason = ocr_unreadable).
/// </summary>
public interface IHomeworkOcrAdapter
{
    Task<HomeworkOcrResult> ExtractAsync(HomeworkOcrRequest request, CancellationToken ct = default);
}

public sealed record HomeworkOcrRequest(
    Stream ImageStream,
    string ContentType,
    string TutorLanguage,
    Guid CorrelationId);

public enum OcrOutcome
{
    Extracted,
    Unreadable,
}

public sealed record HomeworkOcrResult(
    OcrOutcome Outcome,
    string ExtractedText,
    double Confidence,
    Guid? AdapterBindingId,
    string ProviderIdentifier);

/// <summary>
/// Local-mode OCR stub: reads up to 4 KB of the upload, looks for ASCII
/// text, and returns it as the "extracted" text. Anything that decodes to
/// fewer than 4 printable characters is treated as <see cref="OcrOutcome.Unreadable"/>
/// so the refusal path can be exercised by tests with a tiny binary fixture.
/// Production swaps this binding for the Phase 2 OCR/vision adapter.
/// </summary>
public sealed class LocalEchoOcrAdapter : IHomeworkOcrAdapter
{
    public const int SampleBytes = 4096;
    public const int MinPrintableChars = 4;

    public async Task<HomeworkOcrResult> ExtractAsync(HomeworkOcrRequest request, CancellationToken ct = default)
    {
        var buffer = new byte[SampleBytes];
        var read = await request.ImageStream.ReadAsync(buffer.AsMemory(0, SampleBytes), ct);
        if (read <= 0)
            return new HomeworkOcrResult(OcrOutcome.Unreadable, string.Empty, 0d, null, "local-echo-ocr");

        var span = buffer.AsSpan(0, read);
        var asciiOnly = new StringBuilder();
        foreach (var b in span)
        {
            if (b == 10 || b == 13 || (b >= 32 && b < 127)) asciiOnly.Append((char)b);
        }
        var text = asciiOnly.ToString().Trim();
        if (text.Length < MinPrintableChars)
            return new HomeworkOcrResult(OcrOutcome.Unreadable, string.Empty, 0d, null, "local-echo-ocr");

        return new HomeworkOcrResult(
            Outcome: OcrOutcome.Extracted,
            ExtractedText: text,
            Confidence: 0.78,
            AdapterBindingId: null,
            ProviderIdentifier: "local-echo-ocr");
    }
}

/// <summary>
/// T114 (US7) — Server-side PII redaction policy for homework submissions.
///
/// FR-027 (constitution): face-flag metadata sent by the client is a UX
/// nudge, not a privacy guarantee. The server MUST apply its own redaction
/// rules before any text leaves the facade for the tutor runtime. The
/// stripper covers the high-frequency K-12 PII shapes:
///   - Saudi Arabia / Gulf phone numbers (10–13 digits with optional +)
///   - National ID numbers (10-digit blocks at word boundaries)
///   - email addresses
///   - student handles prefixed by '@'
///   - URLs (http/https) — common in homework photos that include a link
/// The redaction collapses each match to a localised placeholder so the
/// downstream guardrail chain still sees the surrounding context.
/// </summary>
public static class HomeworkTextRedactor
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"\+?\d[\d\s\-]{7,14}\d", RegexOptions.Compiled);
    private static readonly Regex NationalId = new(@"\b\d{10}\b", RegexOptions.Compiled);
    private static readonly Regex Handle = new(@"@[A-Za-z0-9_]{3,}", RegexOptions.Compiled);

    public const string Placeholder = "[REDACTED]";

    public static (string Text, IReadOnlyList<string> Categories) Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return (string.Empty, Array.Empty<string>());

        var categories = new List<string>();
        var text = input;

        text = ReplaceWith(text, Email, "email", categories);
        text = ReplaceWith(text, Url, "url", categories);
        // Phone must run before NationalId because a 10-digit Gulf mobile
        // (e.g. 0512345678) also matches the NationalId 10-digit shape, and
        // the more specific category for that number is the phone.
        text = ReplaceWith(text, Phone, "phone", categories);
        text = ReplaceWith(text, NationalId, "national_id", categories);
        text = ReplaceWith(text, Handle, "handle", categories);

        return (text, categories);
    }

    private static string ReplaceWith(string input, Regex pattern, string label, List<string> categories)
    {
        if (!pattern.IsMatch(input)) return input;
        if (!categories.Contains(label)) categories.Add(label);
        return pattern.Replace(input, Placeholder);
    }
}

public static class HomeworkOcrAdapterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3HomeworkOcrAdapter(this IServiceCollection services)
    {
        services.AddSingleton<IHomeworkOcrAdapter, LocalEchoOcrAdapter>();
        return services;
    }
}
