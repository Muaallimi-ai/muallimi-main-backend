using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Exams.ExamAdministration;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.Exams.ExamResults;

/// <summary>
/// T127 / T131 (US6) — ExamAutoGrader.
///
/// Auto-grades a submission for the three objective question types
/// required by the contract — multiple choice, true/false, and
/// fill-in-blank — and writes the score + per-question correctness back
/// to the <see cref="ExamSubmission"/> row. On completion it emits an
/// <c>exam_answered</c> session event through the Phase 3 transport so
/// the Phase 4 mastery pipeline consumes exam results using the same
/// outbox the quiz/mock-test paths do. Fill-in-blank comparison is
/// case-insensitive and ignores surrounding whitespace so the rubric
/// tolerates minor variation without leaving the objective scope.
/// </summary>
public sealed record GradedAnswer(Guid ExamQuestionId, bool IsCorrect, decimal AwardedPoints);

public sealed record AutoGradingResult(
    decimal Score,
    decimal MaxScore,
    IReadOnlyList<GradedAnswer> PerQuestion);

public interface IExamAutoGrader
{
    Task<AutoGradingResult> GradeAsync(
        ExamSubmission submission,
        IReadOnlyList<ExamQuestion> questions,
        Guid studentSessionId,
        Guid subjectId,
        Guid? topicId,
        string planTierSnapshot,
        CancellationToken ct = default);
}

public sealed class ExamAutoGrader : IExamAutoGrader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IExamSubmissionRepository _submissions;
    private readonly IExamEventEmitter _events;

    public ExamAutoGrader(IExamSubmissionRepository submissions, IExamEventEmitter events)
    {
        _submissions = submissions;
        _events = events;
    }

    public async Task<AutoGradingResult> GradeAsync(
        ExamSubmission submission,
        IReadOnlyList<ExamQuestion> questions,
        Guid studentSessionId,
        Guid subjectId,
        Guid? topicId,
        string planTierSnapshot,
        CancellationToken ct = default)
    {
        var answersLookup = DeserialiseAnswers(submission.Answers);
        var graded = new List<GradedAnswer>(questions.Count);
        decimal score = 0m;
        decimal max = 0m;

        foreach (var q in questions)
        {
            max += q.Points;
            answersLookup.TryGetValue(q.ExamQuestionId, out var answer);
            var correct = EvaluateAnswer(q, answer);
            var awarded = correct ? q.Points : 0m;
            if (correct) score += awarded;
            graded.Add(new GradedAnswer(q.ExamQuestionId, correct, awarded));
        }

        submission.Answers = JsonSerializer.Serialize(new
        {
            per_question = graded.Select(g => new
            {
                exam_question_id = g.ExamQuestionId,
                is_correct = g.IsCorrect,
                awarded_points = g.AwardedPoints,
            }),
            raw = answersLookup.ToDictionary(k => k.Key.ToString("D"), v => v.Value),
        }, JsonOptions);
        submission.Score = score;
        submission.MaxScore = max;
        submission.GradingStatus = "graded";
        submission.GradedAt = DateTime.UtcNow;

        await _events.EmitAsync(submission, studentSessionId, subjectId, topicId, planTierSnapshot, ct);

        await _submissions.SaveChangesAsync(ct);

        return new AutoGradingResult(score, max, graded);
    }

    private static bool EvaluateAnswer(ExamQuestion question, JsonElement? answer)
    {
        if (answer is null) return false;

        using var correctDoc = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(question.CorrectAnswer) ? "{}" : question.CorrectAnswer);
        var correct = correctDoc.RootElement;

        return question.QuestionType switch
        {
            "multiple_choice" => CompareMultipleChoice(correct, answer.Value),
            "true_false" => CompareBool(correct, answer.Value),
            "fill_in_blank" => CompareFillInBlank(correct, answer.Value),
            _ => false,
        };
    }

    private static bool CompareMultipleChoice(JsonElement correct, JsonElement answer)
    {
        var correctKey = ExtractStringField(correct, "option_id");
        var answerKey = answer.ValueKind == JsonValueKind.Object
            ? ExtractStringField(answer, "option_id")
            : (answer.ValueKind == JsonValueKind.String ? answer.GetString() : null);
        return !string.IsNullOrWhiteSpace(correctKey)
            && string.Equals(correctKey, answerKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CompareBool(JsonElement correct, JsonElement answer)
    {
        var correctVal = ExtractBool(correct, "value");
        var answerVal = answer.ValueKind == JsonValueKind.Object
            ? ExtractBool(answer, "value")
            : (answer.ValueKind is JsonValueKind.True or JsonValueKind.False ? answer.GetBoolean() : (bool?)null);
        return correctVal is not null && answerVal is not null && correctVal == answerVal;
    }

    private static bool CompareFillInBlank(JsonElement correct, JsonElement answer)
    {
        var accepted = ExtractStringArray(correct, "accepted");
        if (accepted.Count == 0)
        {
            var single = ExtractStringField(correct, "value");
            if (!string.IsNullOrWhiteSpace(single)) accepted = new List<string> { single! };
        }

        var raw = answer.ValueKind == JsonValueKind.Object
            ? ExtractStringField(answer, "value")
            : (answer.ValueKind == JsonValueKind.String ? answer.GetString() : null);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var normalised = raw.Trim().ToLowerInvariant();
        return accepted.Any(a => string.Equals(a.Trim().ToLowerInvariant(), normalised, StringComparison.Ordinal));
    }

    private static Dictionary<Guid, JsonElement> DeserialiseAnswers(string json)
    {
        var result = new Dictionary<Guid, JsonElement>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement answersArray;
        if (root.ValueKind == JsonValueKind.Array)
        {
            answersArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("answers", out var answersProp)
            && answersProp.ValueKind == JsonValueKind.Array)
        {
            answersArray = answersProp;
        }
        else
        {
            return result;
        }

        foreach (var item in answersArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("exam_question_id", out var idProp)) continue;
            if (!Guid.TryParse(idProp.GetString(), out var questionId)) continue;
            if (!item.TryGetProperty("answer", out var answerProp)) continue;
            result[questionId] = answerProp.Clone();
        }

        return result;
    }

    private static string? ExtractStringField(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return null;
    }

    private static bool? ExtractBool(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return v.GetBoolean();
        }
        return null;
    }

    private static List<string> ExtractStringArray(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.ValueKind != JsonValueKind.Object) return list;
        if (!el.TryGetProperty(name, out var v)) return list;
        if (v.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }
}

public static class ExamAutoGraderServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5ExamAutoGrader(this IServiceCollection services)
    {
        services.AddScoped<IExamAutoGrader, ExamAutoGrader>();
        return services;
    }
}
