using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.LessonRetrieval;

/// <summary>
/// T058 (US2) — Integration assertion that the Phase 1 teacher voice
/// profile identifier returned by Study mode is disjoint from every Phase
/// 2 AI tutor voice profile identifier. The ai-service owns the AI tutor
/// adapter (<c>LocalVoiceProfileAdapter</c>) and pins
/// <c>ai-tutor-voice-v1</c> — the main-backend mirror here keeps the two
/// sets in sync so a divergence trips CI, not a live session.
///
/// Constitution rule: every tutor-facing surface must visibly distinguish
/// the Phase 1 teacher voice from the Phase 2 AI tutor voice.
/// </summary>
public class VoiceProfileDisjointTests
{
    // Mirrors Muallimi.AiService.Infrastructure.Adapters.VoiceProfile
    // .LocalVoiceProfileAdapter.AiTutorVoiceProfileId — FR-019 enforces
    // bidirectional disjointness so the same list lives on both sides
    // of the boundary and drift shows up as a failed test.
    private static readonly string[] Phase2AiTutorVoiceProfileIds =
    {
        "ai-tutor-voice-v1",
    };

    [Fact]
    public void Phase1_Teacher_Voice_Set_Intersects_Nothing_In_Phase2_AI_Tutor_Set()
    {
        var intersection = Phase1TeacherVoiceProfiles.All
            .Intersect(Phase2AiTutorVoiceProfileIds)
            .ToArray();
        Assert.Empty(intersection);
    }

    [Fact]
    public void All_Resolver_Outputs_Are_Phase1_Sourced()
    {
        var subjects = Enum.GetValues<Subject>();
        var languages = Enum.GetValues<TutorLanguage>();

        foreach (var subject in subjects)
        {
            foreach (var language in languages)
            {
                var id = Phase1TeacherVoiceProfiles.Resolve(subject, language);
                Assert.Contains(id, Phase1TeacherVoiceProfiles.All);
                Assert.DoesNotContain(id, Phase2AiTutorVoiceProfileIds);
            }
        }
    }

    [Fact]
    public void Lesson_Viewer_State_Carries_Teacher_Voice_Profile_Column()
    {
        // The persisted per-session viewer state MUST record which
        // teacher voice profile was bound so a resume never rebinds to
        // the AI tutor voice by accident (T049 + FR-019).
        var prop = typeof(LessonViewerState).GetProperty(
            nameof(LessonViewerState.TeacherVoiceProfileId),
            BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        Assert.Equal(typeof(string), Nullable.GetUnderlyingType(prop!.PropertyType) ?? prop.PropertyType);
    }

    [Fact]
    public void Lesson_Viewer_Response_Source_Field_Is_Stable_Constant()
    {
        // The wire-level contract pins the source label so downstream
        // analytics / readiness dashboards can key on it without parsing
        // heuristics.
        Assert.Equal("phase1_curriculum", Phase1TeacherVoiceProfiles.Source);
    }
}
