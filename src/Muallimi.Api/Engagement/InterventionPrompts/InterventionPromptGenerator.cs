using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.AtRiskDetection;
using Muallimi.Api.Engagement.FocusAreas;
using Muallimi.Api.Engagement.WeeklyReports;

namespace Muallimi.Api.Engagement.InterventionPrompts;

/// <summary>
/// T147 (US8) — InterventionPromptGenerator.
///
/// Invokes <see cref="IPhase4TutorRuntimeClient"/> with the reserved
/// <c>intervention_prompt</c> prompt key, once in Arabic and once in
/// English. Both passes flow through the Phase 2 guardrail chain. A
/// neutral-language red-team check runs on each generated body — any banned
/// shaming/punitive token forces a refusal so the row is never written.
///
/// Grounding: the prompt is pinned to the originating at-risk evidence and
/// to the deepest concrete focus-area deep link the student already has so
/// the next-step always resolves to a real Phase 1 curriculum node.
/// </summary>
public interface IInterventionPromptGenerator
{
    Task<InterventionPromptGenerationResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        Guid interventionPromptId,
        AtRiskTriggeringEvidence evidence,
        FocusAreaDeepLinkValidation deepLink,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record InterventionPromptGenerationResult(
    string BodyAr,
    string BodyEn,
    Guid GuardrailDecisionTrailId,
    string FinalStage);

public sealed class InterventionPromptGenerator : IInterventionPromptGenerator
{
    public const string PromptKey = "intervention_prompt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Tokens that would breach the neutral-language invariant (T141 red-team
    /// check). The list is intentionally short and conservative — any match
    /// forces a refusal so the row is never persisted.
    /// </summary>
    public static readonly IReadOnlyList<string> BannedShamingTokens = new[]
    {
        // English
        "lazy", "stupid", "failure", "bad student", "useless", "fail",
        "ashamed", "shame", "punish", "punishment", "naughty", "behind",
        // Arabic
        "كسلان", "غبي", "فاشل", "تكاسل", "فشل", "عقاب", "عاقب", "تأخر", "متأخر",
    };

    private readonly IPhase4TutorRuntimeClient _tutor;
    private readonly IGuardrailDecisionTrailStore _trails;

    public InterventionPromptGenerator(
        IPhase4TutorRuntimeClient tutor,
        IGuardrailDecisionTrailStore trails)
    {
        _tutor = tutor;
        _trails = trails;
    }

    public async Task<InterventionPromptGenerationResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        Guid interventionPromptId,
        AtRiskTriggeringEvidence evidence,
        FocusAreaDeepLinkValidation deepLink,
        string correlationId,
        CancellationToken ct = default)
    {
        var grounding = new
        {
            sustained_low_mastery = evidence.SustainedLowMastery,
            lowest_mastery_score = evidence.LowestMasteryScore,
            repeated_refusal = evidence.RepeatedRefusal,
            max_refusal_count_on_topic = evidence.MaxRefusalCountOnTopic,
            declined_engagement = evidence.DeclinedEngagement,
            failed_mock_tests = evidence.FailedMockTests,
            failed_mock_test_count = evidence.FailedMockTestCount,
            phase3_mode = deepLink.Phase3Mode,
            curriculum_node_path = deepLink.CurriculumNodePath,
        };

        var ar = await _tutor.GenerateAsync(new Phase4GenerationRequest(
            PromptKey: PromptKey,
            Language: "ar",
            Tenant: tenantId.ToString("D"),
            Student: studentId.ToString("D"),
            CorrelationId: correlationId,
            Grounding: grounding), ct);

        var en = await _tutor.GenerateAsync(new Phase4GenerationRequest(
            PromptKey: PromptKey,
            Language: "en",
            Tenant: tenantId.ToString("D"),
            Student: studentId.ToString("D"),
            CorrelationId: correlationId,
            Grounding: grounding), ct);

        var bodyAr = string.IsNullOrWhiteSpace(ar.Body) ? RenderFallback("ar") : ar.Body;
        var bodyEn = string.IsNullOrWhiteSpace(en.Body) ? RenderFallback("en") : en.Body;

        var redTeamArViolations = ContainsBannedTokens(bodyAr);
        var redTeamEnViolations = ContainsBannedTokens(bodyEn);

        var finalStage = PickFinalStage(
            ar.GuardrailFinalStage,
            en.GuardrailFinalStage,
            redTeamArViolations.Count > 0 || redTeamEnViolations.Count > 0);

        var combinedTrail = new
        {
            ar = new { final_stage = ar.GuardrailFinalStage, chain = TryParse(ar.GuardrailChainOutput) },
            en = new { final_stage = en.GuardrailFinalStage, chain = TryParse(en.GuardrailChainOutput) },
            red_team = new
            {
                ar_violations = redTeamArViolations,
                en_violations = redTeamEnViolations,
            },
            grounding,
        };

        var trailJson = JsonSerializer.Serialize(combinedTrail, JsonOptions);

        var trailId = await _trails.RecordAsync(
            tenantId: tenantId,
            artefactKind: GuardrailDecisionTrailArtefactKinds.InterventionPrompt,
            artefactId: interventionPromptId,
            promptKey: PromptKey,
            chainOutputJson: trailJson,
            finalStage: finalStage,
            language: "bilingual",
            correlationId: correlationId,
            ct: ct);

        return new InterventionPromptGenerationResult(bodyAr, bodyEn, trailId, finalStage);
    }

    public static IReadOnlyList<string> ContainsBannedTokens(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
        var lowered = body.ToLowerInvariant();
        var hits = new List<string>();
        foreach (var token in BannedShamingTokens)
        {
            if (lowered.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(token);
            }
        }
        return hits;
    }

    private static JsonElement TryParse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}").RootElement;
        try
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private static string PickFinalStage(string ar, string en, bool redTeamRefusal)
    {
        if (redTeamRefusal) return "refuse";
        if (ar == "pass" && en == "pass") return "pass";
        if (ar == "refuse" || en == "refuse") return "refuse";
        if (ar == "revise" || en == "revise") return "revise";
        return "pending";
    }

    private static string RenderFallback(string language)
    {
        // Neutral, supportive defaults used when the tutor runtime returns
        // an empty body. Both copies are explicitly non-shaming and steer
        // the student toward the suggested next step.
        return language == "ar"
            ? "لنواصل التقدم بخطوة صغيرة هذا الأسبوع. اختر الخطوة المقترحة لمتابعة التدرّب."
            : "Let's keep moving forward with one small step this week. Tap the suggested next step to keep practicing.";
    }
}

public static class InterventionPromptGeneratorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4InterventionPromptGenerator(this IServiceCollection services)
    {
        services.AddScoped<IInterventionPromptGenerator, InterventionPromptGenerator>();
        return services;
    }
}
