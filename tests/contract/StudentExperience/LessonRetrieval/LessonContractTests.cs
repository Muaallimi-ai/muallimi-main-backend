using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.LessonRetrieval;

/// <summary>
/// T046 (US2) — Contract for GET /student/study/lessons/{lesson_id}.
///
/// Shape contract: a lesson viewer payload with bilingual display names,
/// per-block content tracks, evidence refs, and the teacher voice profile
/// id sourced from Phase 1. The test asserts
/// <c>teacher_voice_profile_source == "phase1_curriculum"</c> and that the
/// chosen id is disjoint from the Phase 2 AI tutor voice profile id
/// (<c>ai-tutor-voice-v1</c>, mirrored from the ai-service adapter).
/// </summary>
public class LessonContractTests
{
    private const string Phase2AiTutorVoiceProfileId = "ai-tutor-voice-v1";

    [Fact]
    public void LessonViewerResponse_Shape_Matches_Contract()
    {
        var props = typeof(LessonViewerResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("LessonId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("ChapterId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("DisplayNameAr", props);
        Assert.Contains("DisplayNameEn", props);
        Assert.Contains("ContentBlocks", props);
        Assert.Contains("TeacherVoiceProfileId", props);
        Assert.Contains("TeacherVoiceProfileSource", props);
        Assert.Contains("EvidenceRefs", props);
        Assert.Contains("ApprovalState", props);
        Assert.Contains("CorrelationId", props);
    }

    [Fact]
    public void ContentBlock_Carries_Contract_Fields()
    {
        var props = typeof(LessonContentBlock)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("BlockType", props);
        Assert.Contains("Language", props);
        Assert.Contains("TextPayload", props);
        Assert.Contains("MediaReference", props);
        Assert.Contains("CaptionTrackReference", props);
        Assert.Contains("TranscriptReference", props);
    }

    [Fact]
    public void Teacher_Voice_Profile_Source_Is_Phase1_Curriculum()
    {
        Assert.Equal("phase1_curriculum", Phase1TeacherVoiceProfiles.Source);
    }

    [Fact]
    public void Every_Phase1_Teacher_Voice_Profile_Is_Disjoint_From_Phase2_AI_Tutor()
    {
        Assert.DoesNotContain(Phase2AiTutorVoiceProfileId, Phase1TeacherVoiceProfiles.All);
    }

    [Theory]
    [InlineData(Subject.Mathematics,    TutorLanguage.Ar)]
    [InlineData(Subject.Mathematics,    TutorLanguage.En)]
    [InlineData(Subject.Science,        TutorLanguage.Ar)]
    [InlineData(Subject.Science,        TutorLanguage.En)]
    [InlineData(Subject.ArabicLanguage, TutorLanguage.Ar)]
    [InlineData(Subject.EnglishLanguage,TutorLanguage.En)]
    public void Resolved_Teacher_Voice_Is_Phase1_Sourced_And_Not_AI_Tutor(
        Subject subject, TutorLanguage tutorLanguage)
    {
        var id = Phase1TeacherVoiceProfiles.Resolve(subject, tutorLanguage);

        Assert.Contains(id, Phase1TeacherVoiceProfiles.All);
        Assert.NotEqual(Phase2AiTutorVoiceProfileId, id);
    }

    [Fact]
    public void Evidence_Ref_Carries_Chunk_And_Source_Uri()
    {
        var props = typeof(LessonEvidenceRef)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("ChunkId", props);
        Assert.Contains("SourceUri", props);
    }
}
