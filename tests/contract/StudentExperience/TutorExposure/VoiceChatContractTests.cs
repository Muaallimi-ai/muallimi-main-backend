using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.TutorExposure;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.TutorExposure;

/// <summary>
/// T071 (US4) — Contract for POST /student/tutor/voice.
///
/// The contract requires:
///   - a multipart form with session_id, correlation_id, turn_number,
///     audio_blob (binary), codec, tutor_language;
///   - a JSON response carrying turn_number, transcript_text, answer_text,
///     voice_playback_reference, voice_profile_id, voice_profile_source
///     (= "phase2_ai_tutor"), final_outcome, confidence_signal,
///     evidence_refs, ai_request_record_id, refusal_text_ar, refusal_text_en;
///   - voice_profile_id MUST resolve to a Phase 2 AI tutor profile id and MUST
///     NOT collide with any Phase 1 teacher voice id (FR-019);
///   - the same SSE confidence enum + final outcome enum as the text
///     contract — student-facing surfaces never leak numeric thresholds.
/// </summary>
public class VoiceChatContractTests
{
    [Fact]
    public void Voice_Response_Shape_Matches_Contract()
    {
        var props = typeof(TutorVoiceResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("TurnNumber", props);
        Assert.Contains("TranscriptText", props);
        Assert.Contains("AnswerText", props);
        Assert.Contains("VoicePlaybackReference", props);
        Assert.Contains("VoiceProfileId", props);
        Assert.Contains("VoiceProfileSource", props);
        Assert.Contains("FinalOutcome", props);
        Assert.Contains("ConfidenceSignal", props);
        Assert.Contains("EvidenceRefs", props);
        Assert.Contains("AiRequestRecordId", props);
        Assert.Contains("RefusalTextAr", props);
        Assert.Contains("RefusalTextEn", props);
    }

    [Fact]
    public void Voice_Profile_Source_Is_Pinned_To_Phase2_Ai_Tutor()
    {
        // Constitution rule: every voice answer must visibly carry the
        // Phase 2 AI tutor source label so analytics and the UI can
        // distinguish it from the Phase 1 teacher voice (FR-019).
        Assert.Equal("phase2_ai_tutor", Phase2AiTutorVoiceProfiles.Source);
    }

    [Fact]
    public void Default_Voice_Profile_Id_Is_In_Pinned_AI_Tutor_Set()
    {
        Assert.Contains(
            Phase2AiTutorVoiceProfiles.DefaultProfileId,
            Phase2AiTutorVoiceProfiles.All);
        Assert.True(Phase2AiTutorVoiceProfiles.IsAiTutorProfileId(
            Phase2AiTutorVoiceProfiles.DefaultProfileId));
        Assert.False(Phase2AiTutorVoiceProfiles.IsAiTutorProfileId(null));
        Assert.False(Phase2AiTutorVoiceProfiles.IsAiTutorProfileId(""));
    }

    [Fact]
    public void Multipart_Request_Field_Names_Match_Contract()
    {
        var props = typeof(TutorVoiceRequestParts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("CorrelationId", props);
        Assert.Contains("TurnNumber", props);
        Assert.Contains("Codec", props);
        Assert.Contains("TutorLanguage", props);
        Assert.Contains("AudioBlobFieldName", props);

        Assert.Equal("audio_blob", TutorVoiceRequestParts.DefaultAudioBlobFieldName);
    }

    [Fact]
    public void Accepted_Capture_Codecs_Cover_Opus_Webm_And_Ogg()
    {
        Assert.Contains("audio/webm;codecs=opus", TutorVoiceMediaTypes.AcceptedCaptureCodecs);
        Assert.Contains("audio/webm", TutorVoiceMediaTypes.AcceptedCaptureCodecs);
        Assert.Contains("audio/ogg;codecs=opus", TutorVoiceMediaTypes.AcceptedCaptureCodecs);
        Assert.True(TutorVoiceMediaTypes.IsAcceptedCaptureCodec("audio/webm"));
        Assert.False(TutorVoiceMediaTypes.IsAcceptedCaptureCodec(null));
        Assert.False(TutorVoiceMediaTypes.IsAcceptedCaptureCodec(""));
    }

    [Fact]
    public void Voice_Endpoint_Reuses_Text_Surface_Confidence_Enum()
    {
        // Voice answers MUST surface the same confidence enum as text
        // (cache_hit | high_confidence | low_confidence | refused) so the
        // UI can render one badge component for both modalities.
        Assert.Contains("cache_hit", TutorTextSseEvents.AllowedConfidenceSignals);
        Assert.Contains("high_confidence", TutorTextSseEvents.AllowedConfidenceSignals);
        Assert.Contains("low_confidence", TutorTextSseEvents.AllowedConfidenceSignals);
        Assert.Contains("refused", TutorTextSseEvents.AllowedConfidenceSignals);
    }

    [Fact]
    public void Voice_Endpoint_Reuses_Text_Surface_Final_Outcome_Enum()
    {
        Assert.Equal(
            new[] { "answered", "refused", "fallback" }.ToHashSet(),
            TutorTextSseEvents.AllowedFinalOutcomes.ToHashSet());
    }

    [Fact]
    public void Voice_Facade_Has_No_Local_Answer_Generation_Method()
    {
        // FR-019 invariant: TTS conversion of upstream text is allowed,
        // but composing answer text locally is not. The endpoint MUST not
        // expose any method that hints at answer-text generation.
        var methodNames = typeof(TutorVoiceEndpoint)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(methodNames, n => n.Contains("GenerateAnswer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methodNames, n => n.Contains("ComposeAnswer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(methodNames, n => n.Equals("GenerateText", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Voice_Facade_Calls_Phase2_Tutor_Runtime_Through_AskAsync()
    {
        // The facade MUST go through the Phase 2 runtime client; a direct
        // HTTP call would bypass the guardrail chain.
        var helper = typeof(TutorVoiceEndpoint).GetMethod(
            nameof(TutorVoiceEndpoint.CallTutorRuntimeWithVoiceAsync),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(helper);
    }

    [Fact]
    public void Refusal_Outcome_Carries_Bilingual_Refusal_Text_On_Voice_Surface()
    {
        // The voice contract reuses TutorTextFinalPayload's refusal-localised
        // invariant — refusals always need both Arabic + English text.
        var payload = new TutorTextFinalPayload(
            FinalOutcome: "refused",
            GuardrailFinalStage: "out_of_scope",
            AiRequestRecordId: Guid.NewGuid().ToString(),
            RefusalTextAr: "هذا السؤال خارج نطاق درسك المعتمد.",
            RefusalTextEn: "This question is outside your approved lesson.");
        Assert.True(TutorTextSseEvents.IsValidFinal(payload));
    }
}
