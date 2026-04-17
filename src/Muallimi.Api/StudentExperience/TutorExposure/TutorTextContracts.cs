using System;
using System.Collections.Generic;

namespace Muallimi.Api.StudentExperience.TutorExposure;

/// <summary>
/// T060 / T059 (US3) — Wire shapes and SSE envelope constants for the
/// student-facing text tutor chat surface.
///
/// Mirrors
/// <c>specs/005-student-learning-experience/contracts/student-tutor-chat-contract.md</c>.
/// The facade never generates answers itself; upstream is the Phase 2
/// <c>ai.tutor.runtime</c> contract, consumed unchanged through
/// <see cref="ITutorRuntimeClient"/>.
/// </summary>
public sealed record TutorTextRequest(
    Guid SessionId,
    Guid CorrelationId,
    int TurnNumber,
    string QuestionText,
    string TutorLanguage);

public sealed record TutorTextEvidenceRef(
    string ChunkId,
    string SourceUri);

public sealed record TutorTextConfidencePayload(
    string ConfidenceSignal);

public sealed record TutorTextFinalPayload(
    string FinalOutcome,
    string GuardrailFinalStage,
    string AiRequestRecordId,
    string? RefusalTextAr,
    string? RefusalTextEn);

/// <summary>
/// SSE event names and allowed enum values, surfaced as a static catalogue
/// so the contract test suite can assert on the same strings the endpoint
/// emits without duplicating literals.
/// </summary>
public static class TutorTextSseEvents
{
    public const string Delta = "delta";
    public const string Evidence = "evidence";
    public const string Confidence = "confidence";
    public const string Final = "final";

    public static readonly IReadOnlyList<string> EmissionOrder = new[]
    {
        Delta, Evidence, Confidence, Final,
    };

    public static readonly IReadOnlySet<string> AllowedConfidenceSignals = new HashSet<string>
    {
        "cache_hit", "high_confidence", "low_confidence", "refused",
    };

    public static readonly IReadOnlySet<string> AllowedFinalOutcomes = new HashSet<string>
    {
        "answered", "refused", "fallback",
    };

    public static bool IsTerminal(string eventName) =>
        string.Equals(eventName, Final, StringComparison.Ordinal);

    /// <summary>
    /// Validates the constitution rule that a refused final event MUST carry
    /// localised refusal text in both Arabic and English; answered and
    /// fallback outcomes MUST leave both refusal fields null.
    /// </summary>
    public static bool IsValidFinal(TutorTextFinalPayload payload)
    {
        if (!AllowedFinalOutcomes.Contains(payload.FinalOutcome)) return false;
        var refused = string.Equals(payload.FinalOutcome, "refused", StringComparison.Ordinal);
        if (refused)
        {
            return !string.IsNullOrWhiteSpace(payload.RefusalTextAr)
                && !string.IsNullOrWhiteSpace(payload.RefusalTextEn);
        }
        return payload.RefusalTextAr is null && payload.RefusalTextEn is null;
    }
}
