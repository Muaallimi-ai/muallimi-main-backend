using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T092 (US3) — Weekly report summary generator.
///
/// Invokes <see cref="IPhase4TutorRuntimeClient"/> with the reserved
/// <c>weekly_report_summary</c> prompt key for Arabic and English as two
/// independent passes (no machine translation between them). The Phase 2
/// guardrail chain runs unchanged; a <see cref="IGuardrailDecisionTrailStore"/>
/// row is written per generation so every produced summary is auditable.
///
/// The generator returns the decision trail id for the writing caller to
/// persist on the <c>WeeklyReport</c> row — the artefact FK is committed
/// atomically with the row itself, matching T022's contract.
/// </summary>
public interface IWeeklyReportSummaryGenerator
{
    Task<WeeklyReportSummaryResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        Guid weeklyReportId,
        WeeklyReportAggregate aggregate,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record WeeklyReportSummaryResult(
    string SummaryAr,
    string SummaryEn,
    Guid GuardrailDecisionTrailId,
    string FinalStage);

public sealed class WeeklyReportSummaryGenerator : IWeeklyReportSummaryGenerator
{
    public const string PromptKey = "weekly_report_summary";

    private static readonly JsonSerializerOptions GroundingOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IPhase4TutorRuntimeClient _tutor;
    private readonly IGuardrailDecisionTrailStore _trails;

    public WeeklyReportSummaryGenerator(
        IPhase4TutorRuntimeClient tutor,
        IGuardrailDecisionTrailStore trails)
    {
        _tutor = tutor;
        _trails = trails;
    }

    public async Task<WeeklyReportSummaryResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        Guid weeklyReportId,
        WeeklyReportAggregate aggregate,
        string correlationId,
        CancellationToken ct = default)
    {
        var grounding = new
        {
            mastery_deltas = aggregate.MasteryDeltas,
            top_focus_areas = aggregate.TopFocusAreas,
            awarded_badges = aggregate.AwardedBadges,
            evidence_refs = aggregate.EvidenceRefs,
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

        var summaryAr = string.IsNullOrWhiteSpace(ar.Body)
            ? RenderFallback(aggregate, "ar")
            : ar.Body;
        var summaryEn = string.IsNullOrWhiteSpace(en.Body)
            ? RenderFallback(aggregate, "en")
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
            artefactKind: GuardrailDecisionTrailArtefactKinds.WeeklyReportSummary,
            artefactId: weeklyReportId,
            promptKey: PromptKey,
            chainOutputJson: trailJson,
            finalStage: finalStage,
            language: "bilingual",
            correlationId: correlationId,
            ct: ct);

        return new WeeklyReportSummaryResult(summaryAr, summaryEn, trailId, finalStage);
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
        // Any non-pass stage fails the generation; Phase 2 guardrail states
        // we surface are "pass", "refuse", "revise", "pending". If either
        // language is not a clean pass, reflect that in the trail.
        if (ar == "pass" && en == "pass") return "pass";
        if (ar == "refuse" || en == "refuse") return "refuse";
        if (ar == "revise" || en == "revise") return "revise";
        return "pending";
    }

    private static string RenderFallback(WeeklyReportAggregate aggregate, string language)
    {
        var subjects = aggregate.MasteryDeltas.Count;
        var focus = aggregate.TopFocusAreas.Count;
        var badges = aggregate.AwardedBadges.Count;
        return language == "ar"
            ? $"ملخص أسبوعي مبدئي: تم تحديث {subjects} مادة، مع {focus} مواطن تركيز و{badges} شارة جديدة."
            : $"Draft weekly summary: {subjects} subjects updated, {focus} focus areas, {badges} new badges.";
    }
}

public static class WeeklyReportSummaryGeneratorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4WeeklyReportSummaryGenerator(this IServiceCollection services)
    {
        services.AddScoped<IWeeklyReportSummaryGenerator, WeeklyReportSummaryGenerator>();
        return services;
    }
}
