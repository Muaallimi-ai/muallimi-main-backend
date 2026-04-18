using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.WeeklyReports;

namespace Muallimi.Api.Exams.ExamCreation;

/// <summary>
/// T121/T126 (US6) — custom-question guardrail pass-through.
///
/// Custom questions (<c>question_source == "custom"</c>) flow through the
/// Phase 2 guardrail chain before the row is persisted. Local parity is
/// enforced by a conservative lexical red-team sweep — matching the
/// <c>InterventionPromptGenerator</c> approach — so the walkthrough runs
/// without contacting the managed Phase 2 service. Questions that fail
/// are rejected with a deterministic error code; questions that pass
/// leave a <see cref="GuardrailDecisionTrailStore"/> row the UI can link
/// from its incident lookups (FR-046, CR-001 decision trail).
/// </summary>
public sealed record CustomQuestionValidationInput(
    string QuestionTextAr,
    string QuestionTextEn,
    string QuestionType,
    string CorrectAnswerJson,
    string CorrelationId);

public sealed record CustomQuestionValidationResult(
    bool Approved,
    string FinalStage,
    Guid GuardrailDecisionTrailId,
    IReadOnlyList<string> Violations);

public interface ICustomQuestionGuardrailValidator
{
    Task<CustomQuestionValidationResult> ValidateAsync(
        Guid tenantId,
        Guid examQuestionId,
        CustomQuestionValidationInput input,
        CancellationToken ct = default);
}

public sealed class CustomQuestionGuardrailValidator : ICustomQuestionGuardrailValidator
{
    public const string PromptKey = "custom_exam_question_moderation";
    public const string ArtefactKind = "custom_exam_question";

    /// <summary>
    /// Token list kept aligned with <c>InterventionPromptGenerator</c>. The
    /// set is intentionally narrow — any hit forces a refusal so the
    /// <see cref="ExamQuestion"/> row is never persisted.
    /// </summary>
    public static readonly IReadOnlyList<string> BannedTokens = new[]
    {
        // English
        "kill", "stupid", "idiot", "hate", "attack", "hurt",
        "curse", "damn", "racist",
        // Arabic
        "اقتل", "اكره", "غبي", "كره", "عنصري", "لعنة", "تافه",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IGuardrailDecisionTrailStore _trails;

    public CustomQuestionGuardrailValidator(IGuardrailDecisionTrailStore trails)
    {
        _trails = trails;
    }

    public async Task<CustomQuestionValidationResult> ValidateAsync(
        Guid tenantId,
        Guid examQuestionId,
        CustomQuestionValidationInput input,
        CancellationToken ct = default)
    {
        var violations = ScanViolations(input.QuestionTextAr, input.QuestionTextEn);
        var approved = violations.Count == 0;
        var finalStage = approved ? "approved" : "refused";

        var chain = JsonSerializer.Serialize(new
        {
            stages = new object[]
            {
                new { stage = "input_redaction", outcome = "pass", violations = Array.Empty<string>() },
                new { stage = "content_moderation", outcome = approved ? "pass" : "refused", violations },
                new { stage = "final_decision", outcome = finalStage, violations = Array.Empty<string>() },
            },
            prompt_key = PromptKey,
            question_type = input.QuestionType,
        }, JsonOptions);

        var trailId = await _trails.RecordAsync(
            tenantId: tenantId,
            artefactKind: ArtefactKind,
            artefactId: examQuestionId,
            promptKey: PromptKey,
            chainOutputJson: chain,
            finalStage: finalStage,
            language: "ar",
            correlationId: input.CorrelationId,
            ct: ct);

        return new CustomQuestionValidationResult(approved, finalStage, trailId, violations);
    }

    private static IReadOnlyList<string> ScanViolations(string ar, string en)
    {
        var hits = new List<string>();
        var haystack = $"{ar?.ToLowerInvariant()} {en?.ToLowerInvariant()}";
        foreach (var token in BannedTokens)
        {
            if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(token);
            }
        }
        return hits;
    }
}

public static class CustomQuestionGuardrailValidatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5CustomQuestionGuardrailValidator(this IServiceCollection services)
    {
        services.AddScoped<ICustomQuestionGuardrailValidator, CustomQuestionGuardrailValidator>();
        return services;
    }
}
