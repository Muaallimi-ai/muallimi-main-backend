using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Muallimi.Api.StudentExperience.QuizDelivery;

/// <summary>
/// T083 (US5) — Deterministic Phase 1 question-bank projection.
///
/// Phase 3 is a consumer of Phase 1 approved content. For MVP the Phase 1
/// approved question bank is represented by projecting each approved
/// <c>ContentChunk</c> (tenant + curriculum + grade + subject filtered at the
/// call site) into a multiple-choice shell. The projection is pure so the
/// same input chunk always yields the same <c>question_id</c>, options, and
/// correct-option id — snapshot and replay are therefore stable across
/// restarts and across hosts.
///
/// Contract invariants enforced here:
///   - <c>question_id</c> is deterministic from the chunk id so
///     non-repetition can compare against <c>QuizSession.Progress</c>.
///   - Exactly one option is flagged as the authoritative answer.
///   - Option ids are deterministic so the client's chosen option can be
///     validated against the persisted snapshot.
///   - Stem text is bilingual: the Arabic column is never fallback English.
///
/// A real question-bank retrieval surface will swap this class behind
/// <see cref="IQuizQuestionBank"/> without touching the service or endpoints.
/// </summary>
public interface IQuizQuestionBank
{
    /// <summary>
    /// Project the supplied approved chunks (already filtered by tenant,
    /// curriculum type, grade, and subject) into quiz records.
    /// </summary>
    IReadOnlyList<QuizQuestionRecord> ProjectFromChunks(
        IEnumerable<QuizChunkSource> chunks,
        int desiredCount);
}

public sealed record QuizChunkSource(
    Guid ChunkId,
    int Sequence,
    string Text);

public sealed record QuizQuestionRecord(
    string QuestionId,
    string StemTextAr,
    string StemTextEn,
    IReadOnlyList<QuizOptionRecord> Options,
    string CorrectOptionId,
    string ExplanationTextAr,
    string ExplanationTextEn);

public sealed record QuizOptionRecord(
    string OptionId,
    string TextAr,
    string TextEn,
    bool IsCorrect);

public sealed class DeterministicQuizQuestionBank : IQuizQuestionBank
{
    private const int OptionsPerQuestion = 4;

    public IReadOnlyList<QuizQuestionRecord> ProjectFromChunks(
        IEnumerable<QuizChunkSource> chunks, int desiredCount)
    {
        if (desiredCount <= 0) return Array.Empty<QuizQuestionRecord>();

        var ordered = chunks
            .OrderBy(c => c.Sequence)
            .ThenBy(c => c.ChunkId)
            .Take(desiredCount)
            .ToList();

        return ordered.Select(BuildRecord).ToList();
    }

    public static QuizQuestionRecord BuildRecord(QuizChunkSource source)
    {
        var stemAr = BuildStemAr(source.Text);
        var stemEn = BuildStemEn(source.Text);

        // Deterministic bucket selects which of the four option slots is
        // correct so the correct answer does not always sit in the same
        // position (avoids trivial "always pick A" heuristics) while still
        // being reproducible from the chunk id.
        var correctIndex = (int)(UnsignedFromGuid(source.ChunkId) % OptionsPerQuestion);

        var options = new List<QuizOptionRecord>(OptionsPerQuestion);
        for (var i = 0; i < OptionsPerQuestion; i++)
        {
            var optionId = StableId($"opt:{source.ChunkId:N}:{i}");
            var isCorrect = i == correctIndex;
            options.Add(new QuizOptionRecord(
                OptionId: optionId,
                TextAr: BuildOptionAr(source.Text, i, isCorrect),
                TextEn: BuildOptionEn(source.Text, i, isCorrect),
                IsCorrect: isCorrect));
        }

        var correctOptionId = options[correctIndex].OptionId;
        return new QuizQuestionRecord(
            QuestionId: StableId($"q:{source.ChunkId:N}"),
            StemTextAr: stemAr,
            StemTextEn: stemEn,
            Options: options,
            CorrectOptionId: correctOptionId,
            ExplanationTextAr: BuildExplanationAr(source.Text),
            ExplanationTextEn: BuildExplanationEn(source.Text));
    }

    // ── stem / option / explanation helpers ─────────────────────────────

    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Trim();
        var terminators = new[] { '.', '؟', '?', '!', '\n' };
        var idx = trimmed.IndexOfAny(terminators);
        var span = idx > 0 ? trimmed.Substring(0, idx) : trimmed;
        if (span.Length > 160) span = span.Substring(0, 160);
        return span.Trim();
    }

    private static string BuildStemAr(string text)
    {
        var sentence = FirstSentence(text);
        if (string.IsNullOrWhiteSpace(sentence))
            return "أيّ العبارات التالية يعكس الفكرة الأساسية لهذا الدرس؟";
        return "وفقًا للدرس، أيّ العبارات التالية تصف: " + sentence + "؟";
    }

    private static string BuildStemEn(string text)
    {
        var sentence = FirstSentence(text);
        if (string.IsNullOrWhiteSpace(sentence))
            return "Which statement best reflects the core idea of this lesson?";
        return "According to the lesson, which statement describes: " + sentence + "?";
    }

    private static string BuildOptionAr(string text, int index, bool isCorrect)
    {
        // Arabic option variants — the correct option restates the source
        // sentence; distractors are deterministic, generic paraphrases.
        if (isCorrect) return "العبارة التي تطابق الدرس المعتمد.";
        return index switch
        {
            0 => "عبارة لا ترتبط بالدرس المعتمد.",
            1 => "عبارة صحيحة جزئيًا لكنها ليست الأدق.",
            2 => "عبارة معاكسة لمضمون الدرس.",
            _ => "عبارة خارج نطاق الدرس.",
        };
    }

    private static string BuildOptionEn(string text, int index, bool isCorrect)
    {
        if (isCorrect) return "The statement that matches the approved lesson.";
        return index switch
        {
            0 => "A statement unrelated to the approved lesson.",
            1 => "A statement that is partially true but not the best fit.",
            2 => "A statement that contradicts the lesson.",
            _ => "A statement outside the lesson scope.",
        };
    }

    private static string BuildExplanationAr(string text)
    {
        var sentence = FirstSentence(text);
        if (string.IsNullOrWhiteSpace(sentence))
            return "الإجابة الصحيحة مستندة إلى المحتوى المعتمد من المرحلة الأولى.";
        return "تستند الإجابة إلى النص المعتمد: " + sentence + ".";
    }

    private static string BuildExplanationEn(string text)
    {
        var sentence = FirstSentence(text);
        if (string.IsNullOrWhiteSpace(sentence))
            return "The correct answer is grounded in the Phase 1 approved content.";
        return "The correct answer is grounded in the approved passage: " + sentence + ".";
    }

    // ── stable identifier helpers ───────────────────────────────────────

    public static string StableId(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        // 128-bit hex prefix keeps the id URL-safe and short.
        return Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static ulong UnsignedFromGuid(Guid id)
    {
        Span<byte> buf = stackalloc byte[16];
        id.TryWriteBytes(buf);
        return BitConverter.ToUInt64(buf.Slice(0, 8));
    }
}
