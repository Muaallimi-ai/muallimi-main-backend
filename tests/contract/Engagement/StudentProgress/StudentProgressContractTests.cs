using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentProgressSurface;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Engagement.StudentProgress;

/// <summary>
/// T044 (US1) — Contract tests for <c>phase4.student.progress</c>.
///
/// Pins the response shape that
/// <c>specs/006-engagement-progress-parent/contracts/student-progress-contract.md</c>
/// commits to so Phase 4 consumers (frontend, Phase 5 operator tooling)
/// stay stable when the main-backend refactors the underlying storage.
///
/// Also pins the three endpoint routes so the frontend deep link and the
/// parent dashboard hand-off never drift.
/// </summary>
public class StudentProgressContractTests
{
    [Fact]
    public void StudentProgressSummary_Carries_All_Top_Level_Fields()
    {
        var props = PropertyNamesOf<StudentProgressSummary>();
        Assert.Contains("StudentId", props);
        Assert.Contains("CurriculumType", props);
        Assert.Contains("MasteryBySubject", props);
        Assert.Contains("Streak", props);
        Assert.Contains("Badges", props);
        Assert.Contains("FocusAreas", props);
    }

    [Fact]
    public void MasterySubjectSummary_Exposes_Bilingual_Labels_And_Topic_Breakdown()
    {
        var props = PropertyNamesOf<MasterySubjectSummary>();
        Assert.Contains("SubjectId", props);
        Assert.Contains("SubjectLabelAr", props);
        Assert.Contains("SubjectLabelEn", props);
        Assert.Contains("MasteryScore", props);
        Assert.Contains("MasteryBand", props);
        Assert.Contains("TopicBreakdown", props);
    }

    [Fact]
    public void MasteryTopicSummary_Exposes_Bilingual_Labels_And_Score()
    {
        var props = PropertyNamesOf<MasteryTopicSummary>();
        Assert.Contains("TopicId", props);
        Assert.Contains("TopicLabelAr", props);
        Assert.Contains("TopicLabelEn", props);
        Assert.Contains("MasteryScore", props);
        Assert.Contains("MasteryBand", props);
    }

    [Fact]
    public void StreakSummary_Carries_Timezone_Authoritative_Fields()
    {
        var props = PropertyNamesOf<StreakSummary>();
        Assert.Contains("CurrentLength", props);
        Assert.Contains("LongestLength", props);
        Assert.Contains("LastQualifyingDay", props);
        Assert.Contains("FamilyTimezone", props);
    }

    [Fact]
    public void BadgeSummary_Carries_Bilingual_Display_Names_And_Celebration_Bit()
    {
        var props = PropertyNamesOf<BadgeSummary>();
        Assert.Contains("BadgeAwardId", props);
        Assert.Contains("BadgeKey", props);
        Assert.Contains("BadgeCriterionVersion", props);
        Assert.Contains("AwardedAt", props);
        Assert.Contains("DisplayNameAr", props);
        Assert.Contains("DisplayNameEn", props);
        Assert.Contains("CelebrationShown", props);
    }

    [Fact]
    public void FocusAreaSummary_Carries_Bilingual_Rationale_And_NextStep_DeepLink()
    {
        var props = PropertyNamesOf<FocusAreaSummary>();
        Assert.Contains("FocusAreaId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("ChapterId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("RationaleAr", props);
        Assert.Contains("RationaleEn", props);
        Assert.Contains("SuggestedNextStep", props);
    }

    [Fact]
    public void FocusAreaNextStep_Carries_Mode_And_DeepLink()
    {
        var props = PropertyNamesOf<FocusAreaNextStep>();
        Assert.Contains("Phase3Mode", props);
        Assert.Contains("DeepLink", props);
    }

    [Fact]
    public void BadgeCelebrationOutcome_Pins_Three_States()
    {
        var names = Enum.GetNames(typeof(BadgeCelebrationOutcome)).ToHashSet();
        Assert.Contains("Marked", names);
        Assert.Contains("AlreadyShown", names);
        Assert.Contains("NotFound", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/student/progress/summary", StudentProgressEndpoint.Route);
        Assert.Equal(
            "/api/student/progress/focus-area/{focusAreaId:guid}",
            FocusAreaDetailEndpoint.Route);
        Assert.Equal(
            "/api/student/progress/badges/{badgeAwardId:guid}/celebration-shown",
            BadgeCelebrationShownEndpoint.Route);
    }

    private static System.Collections.Generic.HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
