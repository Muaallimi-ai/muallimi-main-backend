using System.Linq;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Api.StudentExperience.TutorExposure;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.TutorExposure;

/// <summary>
/// T080 (US4) — Cross-phase voice-profile identity test.
///
/// Asserts that the Phase 3 live tutor playback voice ids never equal any
/// Phase 1 teacher voice ids. Constitution rule (FR-019): Phase 1 teacher
/// voice profiles are reserved for Study mode narration of approved
/// lessons; Phase 2 AI tutor voice profiles are reserved for live tutor
/// playback. The two sets are disjoint by design and the disjointness is
/// enforced bidirectionally — both directions get their own assertion so
/// drift in either direction trips CI before a live session.
///
/// The Phase 2 AI tutor pinned set lives next to the facade
/// (<see cref="Phase2AiTutorVoiceProfiles.All"/>) and mirrors the
/// ai-service <c>LocalVoiceProfileAdapter.AiTutorVoiceProfileId</c>;
/// VoiceProfileDisjointTests (T058) covers the mirror in the other
/// direction.
/// </summary>
public class VoiceProfileIdentityTests
{
    [Fact]
    public void Phase3_Playback_Voice_Set_Intersects_Nothing_In_Phase1_Teacher_Set()
    {
        var intersection = Phase2AiTutorVoiceProfiles.All
            .Intersect(Phase1TeacherVoiceProfiles.All)
            .ToArray();
        Assert.Empty(intersection);
    }

    [Fact]
    public void Phase1_Teacher_Voice_Ids_Are_Not_Marked_As_Phase2_Ai_Tutor()
    {
        foreach (var teacherId in Phase1TeacherVoiceProfiles.All)
        {
            Assert.False(
                Phase2AiTutorVoiceProfiles.IsAiTutorProfileId(teacherId),
                $"Phase 1 teacher voice id '{teacherId}' must NOT be classified as a Phase 2 AI tutor profile.");
        }
    }

    [Fact]
    public void Phase2_Ai_Tutor_Voice_Profile_Source_Is_Phase2_Ai_Tutor_Constant()
    {
        // The wire-level constant pins the source label so the analytics
        // dashboard and the UI two-voice label can key on it without
        // parsing heuristics.
        Assert.Equal("phase2_ai_tutor", Phase2AiTutorVoiceProfiles.Source);
        Assert.Equal("phase1_curriculum", Phase1TeacherVoiceProfiles.Source);
        Assert.NotEqual(Phase1TeacherVoiceProfiles.Source, Phase2AiTutorVoiceProfiles.Source);
    }

    [Fact]
    public void Local_Synthesizer_Resolves_Default_Profile_To_Pinned_AI_Tutor_Id()
    {
        // The local-mode synthesiser MUST pin its output to the AI tutor
        // voice id; cloud adapters that swap in via DI MUST satisfy the
        // same invariant or the synthesiser fails closed.
        var blobs = new InMemoryVoiceBlobStore();
        var synth = new LocalAnswerAudioSynthesizer(blobs);
        var result = synth
            .SynthesiseAsync(new AnswerAudioRequest(
                AnswerText: "إجابة قصيرة.",
                TutorLanguage: "ar",
                StudentSessionId: System.Guid.NewGuid(),
                CorrelationId: System.Guid.NewGuid()))
            .GetAwaiter()
            .GetResult();

        Assert.NotNull(result.PlaybackReference);
        Assert.Equal(Phase2AiTutorVoiceProfiles.DefaultProfileId, result.VoiceProfileId);
        Assert.DoesNotContain(result.VoiceProfileId!, Phase1TeacherVoiceProfiles.All);
    }

    [Fact]
    public void Local_Synthesizer_Returns_None_For_Empty_Answer_Text()
    {
        // Refusal envelopes carry empty answer text; synthesizing on the
        // refusal path would consume generation tokens, breaking the
        // zero-tokens-on-refusal constitution rule.
        var blobs = new InMemoryVoiceBlobStore();
        var synth = new LocalAnswerAudioSynthesizer(blobs);
        var result = synth
            .SynthesiseAsync(new AnswerAudioRequest(
                AnswerText: string.Empty,
                TutorLanguage: "ar",
                StudentSessionId: System.Guid.NewGuid(),
                CorrelationId: System.Guid.NewGuid()))
            .GetAwaiter()
            .GetResult();

        Assert.Null(result.PlaybackReference);
        Assert.Null(result.VoiceProfileId);
    }
}
