using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.WeeklyReports;

namespace Muallimi.Api.Engagement.FocusAreas;

/// <summary>
/// T109 (US5) — FocusAreaRationaleGenerator.
///
/// Invokes the Phase 2 tutor runtime through
/// <see cref="IPhase4TutorRuntimeClient"/> with the reserved
/// <c>focus_area_rationale</c> prompt key, once in Arabic and once in
/// English (two independent passes — no machine translation between them).
/// Both passes flow through the Phase 2 guardrail chain; a single
/// <see cref="IGuardrailDecisionTrailStore"/> row is written per focus
/// area so the decision trail is atomic with the row FK.
///
/// The produced rationales are parent-facing and student-facing text —
/// they MUST never fabricate a topic. Grounding is therefore scoped to the
/// exact Phase 1 curriculum node the signal collector identified and the
/// observed signal summary; the tutor runtime runs its no-hallucination
/// guard on that grounding.
/// </summary>
public interface IFocusAreaRationaleGenerator
{
    Task<FocusAreaRationaleResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        Guid focusAreaId,
        FocusAreaSignal signal,
        FocusAreaDeepLinkValidation deepLink,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record FocusAreaRationaleResult(
    string RationaleAr,
    string RationaleEn,
    Guid GuardrailDecisionTrailId,
    string FinalStage);

public sealed class FocusAreaRationaleGenerator : IFocusAreaRationaleGenerator
{
    public const string PromptKey = "focus_area_rationale";

    private static readonly JsonSerializerOptions GroundingOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IPhase4TutorRuntimeClient _tutor;
    private readonly IGuardrailDecisionTrailStore _trails;

    public FocusAreaRationaleGenerator(
        IPhase4TutorRuntimeClient tutor,
        IGuardrailDecisionTrailStore trails)
    {
        _tutor = tutor;
        _trails = trails;
    }

    public async Task<FocusAreaRationaleResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        Guid focusAreaId,
        FocusAreaSignal signal,
        FocusAreaDeepLinkValidation deepLink,
        string correlationId,
        CancellationToken ct = default)
    {
        var grounding = new
        {
            curriculum_type = signal.CurriculumType,
            subject_id = signal.SubjectId,
            chapter_id = signal.ChapterId,
            topic_id = signal.TopicId,
            mastery_band = signal.MasteryBand,
            mastery_gap = signal.MasteryGap,
            quiz_error_count = signal.QuizErrorCount,
            homework_help_count = signal.HomeworkHelpCount,
            touched_event_count = signal.TouchedEventCount,
            curriculum_node_path = deepLink.CurriculumNodePath,
            phase3_mode = deepLink.Phase3Mode,
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

        var rationaleAr = string.IsNullOrWhiteSpace(ar.Body)
            ? RenderFallback(signal, "ar")
            : ar.Body;
        var rationaleEn = string.IsNullOrWhiteSpace(en.Body)
            ? RenderFallback(signal, "en")
            : en.Body;

        var finalStage = PickFinalStage(ar.GuardrailFinalStage, en.GuardrailFinalStage);

        var combinedTrail = new
        {
            ar = new { final_stage = ar.GuardrailFinalStage, chain = TryParse(ar.GuardrailChainOutput) },
            en = new { final_stage = en.GuardrailFinalStage, chain = TryParse(en.GuardrailChainOutput) },
            grounding,
        };
        var trailJson = JsonSerializer.Serialize(combinedTrail, GroundingOptions);

        var trailId = await _trails.RecordAsync(
            tenantId: tenantId,
            artefactKind: GuardrailDecisionTrailArtefactKinds.FocusAreaRationale,
            artefactId: focusAreaId,
            promptKey: PromptKey,
            chainOutputJson: trailJson,
            finalStage: finalStage,
            language: "bilingual",
            correlationId: correlationId,
            ct: ct);

        return new FocusAreaRationaleResult(rationaleAr, rationaleEn, trailId, finalStage);
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

    private static string PickFinalStage(string ar, string en)
    {
        if (ar == "pass" && en == "pass") return "pass";
        if (ar == "refuse" || en == "refuse") return "refuse";
        if (ar == "revise" || en == "revise") return "revise";
        return "pending";
    }

    private static string RenderFallback(FocusAreaSignal signal, string language)
    {
        return language == "ar"
            ? $"مواصلة التدرب على هذا الموضوع بناءً على {signal.TouchedEventCount} نشاطًا حديثًا."
            : $"Keep practicing this topic based on {signal.TouchedEventCount} recent activities.";
    }
}

public static class FocusAreaRationaleGeneratorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4FocusAreaRationaleGenerator(this IServiceCollection services)
    {
        services.AddScoped<IFocusAreaRationaleGenerator, FocusAreaRationaleGenerator>();
        return services;
    }
}
