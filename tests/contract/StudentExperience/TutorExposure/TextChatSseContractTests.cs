using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.TutorExposure;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.TutorExposure;

/// <summary>
/// T059 (US3) — Contract for POST /student/tutor/text SSE envelope.
///
/// The contract requires:
///   - a JSON request body with session_id, correlation_id, turn_number,
///     question_text, tutor_language.
///   - an SSE response producing `delta`, `evidence`, `confidence`, and
///     `final` events, in that order, with a single terminal `final` event.
///   - the `final` event payload carrying final_outcome (answered | refused |
///     fallback), guardrail_final_stage, ai_request_record_id, and bilingual
///     refusal text columns (null on answered/fallback).
///   - a `confidence` event whose signal is in the allowed enum set
///     (cache_hit | high_confidence | low_confidence | refused) and does NOT
///     expose internal thresholds (numeric scores, prompt ids, etc).
/// </summary>
public class TextChatSseContractTests
{
    [Fact]
    public void TutorTextRequest_Shape_Matches_Contract()
    {
        var props = typeof(TutorTextRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("CorrelationId", props);
        Assert.Contains("TurnNumber", props);
        Assert.Contains("QuestionText", props);
        Assert.Contains("TutorLanguage", props);
    }

    [Fact]
    public void Allowed_Confidence_Signals_Match_Contract()
    {
        var allowed = TutorTextSseEvents.AllowedConfidenceSignals;
        Assert.Contains("cache_hit", allowed);
        Assert.Contains("high_confidence", allowed);
        Assert.Contains("low_confidence", allowed);
        Assert.Contains("refused", allowed);
        Assert.Equal(4, allowed.Count);
    }

    [Fact]
    public void Allowed_Final_Outcomes_Match_Contract()
    {
        var allowed = TutorTextSseEvents.AllowedFinalOutcomes;
        Assert.Contains("answered", allowed);
        Assert.Contains("refused", allowed);
        Assert.Contains("fallback", allowed);
        Assert.Equal(3, allowed.Count);
    }

    [Fact]
    public void Sse_Event_Names_Match_Contract()
    {
        Assert.Equal("delta", TutorTextSseEvents.Delta);
        Assert.Equal("evidence", TutorTextSseEvents.Evidence);
        Assert.Equal("confidence", TutorTextSseEvents.Confidence);
        Assert.Equal("final", TutorTextSseEvents.Final);
    }

    [Fact]
    public void Final_Event_Payload_Carries_Required_Fields()
    {
        var props = typeof(TutorTextFinalPayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("FinalOutcome", props);
        Assert.Contains("GuardrailFinalStage", props);
        Assert.Contains("AiRequestRecordId", props);
        Assert.Contains("RefusalTextAr", props);
        Assert.Contains("RefusalTextEn", props);
    }

    [Fact]
    public void Evidence_Event_Payload_Carries_Chunk_And_Source_Uri()
    {
        var props = typeof(TutorTextEvidenceRef)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("ChunkId", props);
        Assert.Contains("SourceUri", props);
    }

    [Fact]
    public void Confidence_Signal_Does_Not_Expose_Internal_Thresholds()
    {
        // Constitution rule (FR-020 & spec §US3): the student-facing surface
        // must not leak routing thresholds, prompt ids, or numeric scores.
        var props = typeof(TutorTextConfidencePayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.Single(props);
        Assert.Equal("ConfidenceSignal", props.Single());
    }

    [Fact]
    public void Stream_Ordering_Is_Delta_Then_Evidence_Then_Confidence_Then_Final()
    {
        var order = TutorTextSseEvents.EmissionOrder;
        Assert.Equal(new[] { "delta", "evidence", "confidence", "final" }, order);
    }

    [Fact]
    public void Final_Event_Is_Terminal()
    {
        Assert.True(TutorTextSseEvents.IsTerminal("final"));
        Assert.False(TutorTextSseEvents.IsTerminal("delta"));
        Assert.False(TutorTextSseEvents.IsTerminal("evidence"));
        Assert.False(TutorTextSseEvents.IsTerminal("confidence"));
    }

    [Theory]
    [InlineData("answered", false)]
    [InlineData("fallback", false)]
    [InlineData("refused", true)]
    public void Refusal_Outcome_Requires_Bilingual_Refusal_Text(string outcome, bool requiresRefusalText)
    {
        var payload = new TutorTextFinalPayload(
            FinalOutcome: outcome,
            GuardrailFinalStage: "content_grounding",
            AiRequestRecordId: Guid.NewGuid().ToString(),
            RefusalTextAr: requiresRefusalText ? "لا يمكنني الإجابة على هذا السؤال." : null,
            RefusalTextEn: requiresRefusalText ? "I cannot answer this question." : null);

        Assert.True(TutorTextSseEvents.IsValidFinal(payload),
            "Final payload must satisfy refusal-localisation rule.");
    }

    [Fact]
    public void Refused_Outcome_Without_Bilingual_Refusal_Text_Fails_Validation()
    {
        var missingEnglish = new TutorTextFinalPayload(
            FinalOutcome: "refused",
            GuardrailFinalStage: "grounding",
            AiRequestRecordId: Guid.NewGuid().ToString(),
            RefusalTextAr: "لا يمكنني الإجابة.",
            RefusalTextEn: null);
        Assert.False(TutorTextSseEvents.IsValidFinal(missingEnglish));

        var missingArabic = missingEnglish with { RefusalTextAr = null, RefusalTextEn = "nope" };
        Assert.False(TutorTextSseEvents.IsValidFinal(missingArabic));
    }
}
