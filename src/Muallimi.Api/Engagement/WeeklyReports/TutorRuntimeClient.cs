using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T013 — Phase 4 wrapper around the Phase 2 <c>ai.tutor.runtime</c> contract.
///
/// Phase 4 calls the tutor runtime for three natural-language artefacts:
///   - Weekly report summary (prompt_key=weekly_report_summary)
///   - Focus-area rationale  (prompt_key=focus_area_rationale)
///   - Intervention prompt   (prompt_key=intervention_prompt)
///
/// The wrapper never bypasses the Phase 2 tutor runtime; every call passes
/// through the guardrail chain with a stored decision trail (see
/// GuardrailDecisionTrailStore). The Phase 2 contract is consumed unchanged.
/// </summary>
public interface IPhase4TutorRuntimeClient
{
    Task<Phase4GenerationResult> GenerateAsync(
        Phase4GenerationRequest request,
        CancellationToken ct = default);
}

public sealed record Phase4GenerationRequest(
    string PromptKey,
    string Language,
    string Tenant,
    string Student,
    string CorrelationId,
    object Grounding);

public sealed record Phase4GenerationResult(
    string Body,
    string GuardrailFinalStage,
    string GuardrailChainOutput,
    string CorrelationId);

public sealed class Phase4TutorRuntimeClient : IPhase4TutorRuntimeClient
{
    public const string HttpClientName = "phase4-tutor-runtime";
    private const string StudentPromptPath = "/internal/tutor/ask";

    private readonly HttpClient _http;

    public Phase4TutorRuntimeClient(HttpClient http)
    {
        _http = http;
    }

    public Task<Phase4GenerationResult> GenerateAsync(
        Phase4GenerationRequest request,
        CancellationToken ct = default)
    {
        // Full invocation is wired per user story (US3 weekly report,
        // US5 focus-area rationale, US8 intervention prompt). This
        // foundational stub returns a pending result so DI wiring can be
        // tested before the user-story wiring lands.
        return Task.FromResult(new Phase4GenerationResult(
            Body: string.Empty,
            GuardrailFinalStage: "pending",
            GuardrailChainOutput: "{}",
            CorrelationId: request.CorrelationId));
    }
}

public static class Phase4TutorRuntimeClientServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4TutorRuntimeClient(this IServiceCollection services)
    {
        services.AddHttpClient<IPhase4TutorRuntimeClient, Phase4TutorRuntimeClient>(
            Phase4TutorRuntimeClient.HttpClientName,
            (_, client) =>
            {
                client.BaseAddress = new System.Uri("http://localhost:5080/");
            });
        return services;
    }
}
