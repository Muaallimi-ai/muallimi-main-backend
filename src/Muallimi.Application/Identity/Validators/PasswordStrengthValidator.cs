using System;
using Zxcvbn;

namespace Muallimi.Application.Identity.Validators;

/// <summary>
/// T033 — Password strength validator using the zxcvbn-core port of
/// Dropbox's zxcvbn. Requires a score of at least 3 (0-4 scale). Returns
/// Arabic + English feedback strings so the frontend can render the
/// matching locale without a round trip.
/// </summary>
public interface IPasswordStrengthValidator
{
    PasswordStrengthResult Evaluate(string password, params string[] userInputs);
}

public sealed record PasswordStrengthResult(
    int Score,
    bool IsAcceptable,
    string FeedbackAr,
    string FeedbackEn);

public sealed class ZxcvbnPasswordStrengthValidator : IPasswordStrengthValidator
{
    public const int MinScore = 3;

    public PasswordStrengthResult Evaluate(string password, params string[] userInputs)
    {
        if (string.IsNullOrEmpty(password))
        {
            return new PasswordStrengthResult(
                Score: 0,
                IsAcceptable: false,
                FeedbackAr: "كلمة المرور فارغة.",
                FeedbackEn: "Password is empty.");
        }

        var result = Core.EvaluatePassword(password, userInputs);
        var ar = MapFeedbackToArabic(result.Feedback?.Warning ?? string.Empty, result.Score);
        var en = string.IsNullOrWhiteSpace(result.Feedback?.Warning)
            ? MapScoreToEnglish(result.Score)
            : result.Feedback!.Warning;
        return new PasswordStrengthResult(
            Score: result.Score,
            IsAcceptable: result.Score >= MinScore,
            FeedbackAr: ar,
            FeedbackEn: en);
    }

    private static string MapScoreToEnglish(int score) => score switch
    {
        0 => "Password is very weak.",
        1 => "Password is weak.",
        2 => "Password is moderate.",
        3 => "Password is strong.",
        _ => "Password is very strong.",
    };

    private static string MapFeedbackToArabic(string warning, int score)
    {
        // Map the handful of common zxcvbn warnings to MSA Arabic strings.
        // Fallback is a generic strength phrase so the user always sees
        // something fluent.
        if (string.IsNullOrWhiteSpace(warning))
        {
            return score switch
            {
                0 => "كلمة المرور ضعيفة جدًا.",
                1 => "كلمة المرور ضعيفة.",
                2 => "كلمة المرور متوسطة.",
                3 => "كلمة المرور قوية.",
                _ => "كلمة المرور قوية جدًا.",
            };
        }
        // Best-effort translation of the most frequent warnings.
        if (warning.Contains("common", StringComparison.OrdinalIgnoreCase))
            return "هذه كلمة مرور شائعة جدًا.";
        if (warning.Contains("repeat", StringComparison.OrdinalIgnoreCase))
            return "تكرار الأحرف يجعل كلمة المرور سهلة التخمين.";
        if (warning.Contains("sequence", StringComparison.OrdinalIgnoreCase))
            return "تسلسل الأحرف يجعل كلمة المرور سهلة التخمين.";
        if (warning.Contains("dates", StringComparison.OrdinalIgnoreCase) || warning.Contains("year", StringComparison.OrdinalIgnoreCase))
            return "التواريخ يسهل تخمينها — تجنّب استخدامها.";
        return "كلمة المرور قابلة للتخمين — جرّب كلمة أقوى.";
    }
}
