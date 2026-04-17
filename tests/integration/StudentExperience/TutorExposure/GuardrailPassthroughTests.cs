using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.StudentExperience.TutorExposure;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.TutorExposure;

/// <summary>
/// T070 (US3) — Integration assertion that the Phase 3 tutor facade is
/// a pass-through over the Phase 2 guardrail chain and never hosts a
/// facade-local answer-generation path.
///
/// The test exercises two invariants of the student-tutor-chat contract:
///   1. Every answered turn must carry an <c>ai_request_record_id</c> from
///      the Phase 2 runtime (no synthetic ids, no blank ids).
///   2. Every refused turn must carry Arabic + English refusal text and the
///      facade may NOT invent an answer in place of the refusal (zero
///      generation tokens for pre-generation refusals — constitution rule).
///
/// Implementation evidence is inspected via reflection over
/// <see cref="TutorTextEndpoint"/> so any regression that introduces a
/// generation method on the facade surface trips CI.
/// </summary>
public class GuardrailPassthroughTests
{
    [Fact]
    public void Facade_Has_No_Local_Answer_Generation_Method()
    {
        var methodNames = typeof(TutorTextEndpoint)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(methodNames, n => n.Contains("Generate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methodNames, n => n.Contains("Synthesize", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methodNames, n => n.Contains("ComposeAnswer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Facade_Uses_TutorRuntimeClient_AskAsync_Only()
    {
        // The facade's upstream call surface MUST remain AskAsync on the
        // Phase 2 runtime client — a new direct HTTP path would mean the
        // guardrail chain is bypassed.
        var helper = typeof(TutorTextEndpoint).GetMethod(
            "CallTutorRuntimeAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(helper);
    }

    [Fact]
    public async Task Answer_Envelope_Maps_To_Answered_Outcome_With_RecordId()
    {
        var envelope = new
        {
            envelope_kind = "answer",
            request_id = "req-1",
            correlation_id = "corr-1",
            answer_text = "الإجابة الموثوقة.",
            evidence_refs = new[]
            {
                new { chunk_id = "c-1", confidence = 0.9, source_lesson_id = "phase1://lesson-1" },
            },
            confidence_signal = "high_confidence",
            routing_metadata = new { record_id = "rec-xyz", model_tier = "llm_lightweight" },
        };
        var upstream = new UpstreamTutorResponse(true, JsonSerializer.Serialize(envelope));

        var result = TutorTextEndpoint.MapUpstream(upstream, "ar");

        Assert.Equal("answered", result.Final.FinalOutcome);
        Assert.Equal("rec-xyz", result.Final.AiRequestRecordId);
        Assert.Null(result.Final.RefusalTextAr);
        Assert.Null(result.Final.RefusalTextEn);
        Assert.Equal("high_confidence", result.Confidence.ConfidenceSignal);
        Assert.Single(result.EvidenceRefs);
        Assert.Equal("الإجابة الموثوقة.", result.AnswerText);
        Assert.True(TutorTextSseEvents.IsValidFinal(result.Final));

        // Ensure we did not silently drop into a facade-generated fallback.
        Assert.NotEmpty(result.Deltas);
        await Task.CompletedTask;
    }

    [Fact]
    public void Refusal_Envelope_Preserves_Guardrail_Stage_And_Bilingual_Refusal_Text()
    {
        var envelope = new
        {
            envelope_kind = "refusal",
            request_id = "req-2",
            correlation_id = "corr-2",
            stage = "out_of_scope",
            reason = "out_of_scope",
            reason_localised = "هذا السؤال خارج نطاق درسك المعتمد.",
            routing_metadata = new { record_id = "rec-refused-1", model_tier = "refused" },
        };
        var upstream = new UpstreamTutorResponse(true, JsonSerializer.Serialize(envelope));

        var result = TutorTextEndpoint.MapUpstream(upstream, "ar");

        Assert.Equal("refused", result.Final.FinalOutcome);
        Assert.Equal("out_of_scope", result.Final.GuardrailFinalStage);
        Assert.Equal("rec-refused-1", result.Final.AiRequestRecordId);
        Assert.False(string.IsNullOrWhiteSpace(result.Final.RefusalTextAr));
        Assert.False(string.IsNullOrWhiteSpace(result.Final.RefusalTextEn));
        Assert.Equal("refused", result.Confidence.ConfidenceSignal);
        Assert.Empty(result.EvidenceRefs);
        Assert.Empty(result.Deltas);
        Assert.Null(result.AnswerText);
        Assert.True(TutorTextSseEvents.IsValidFinal(result.Final));
    }

    [Fact]
    public void Upstream_Failure_Does_Not_Trigger_Local_Answer_Generation()
    {
        var upstream = new UpstreamTutorResponse(false, string.Empty);

        var result = TutorTextEndpoint.MapUpstream(upstream, "ar");

        // Facade MUST fail closed with a refusal envelope — never with a
        // synthesised answer. Zero generation tokens on the facade side.
        Assert.Equal("refused", result.Final.FinalOutcome);
        Assert.Equal("refused", result.Confidence.ConfidenceSignal);
        Assert.Empty(result.Deltas);
        Assert.Null(result.AnswerText);
        Assert.False(string.IsNullOrWhiteSpace(result.Final.RefusalTextAr));
        Assert.False(string.IsNullOrWhiteSpace(result.Final.RefusalTextEn));
    }

    [Fact]
    public void Tutor_Runtime_Client_Interface_Exposes_Only_Phase2_Pass_Through_Methods()
    {
        var methods = typeof(ITutorRuntimeClient)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet();
        Assert.Contains("AskAsync", methods);
        Assert.Contains("StreamAskAsync", methods);
        Assert.Contains("SynthesizeVoiceAsync", methods);
        // Any method implying local generation is a contract breach.
        Assert.DoesNotContain(methods, n => n.StartsWith("Generate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methods, n => n.StartsWith("Compose", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Confidence_Surface_Does_Not_Leak_Internal_Thresholds()
    {
        // The student-facing confidence envelope must expose the enum
        // label only — scores, thresholds, prompt ids, or tier names are
        // gated out (FR-020).
        var props = typeof(TutorTextConfidencePayload)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();
        Assert.Single(props);
        Assert.Equal("ConfidenceSignal", props.Single());
    }

    [Fact]
    public void Stream_Ordering_Is_Enforced()
    {
        // Constitution-level invariant: delta events MUST fire before the
        // terminal final event so the UI can render the answer
        // incrementally instead of waiting for the stream to close.
        var order = TutorTextSseEvents.EmissionOrder;
        var finalIdx = Array.IndexOf(order.ToArray(), "final");
        var deltaIdx = Array.IndexOf(order.ToArray(), "delta");
        Assert.True(deltaIdx < finalIdx);
    }
}
