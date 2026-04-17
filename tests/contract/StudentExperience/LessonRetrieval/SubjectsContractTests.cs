using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.LessonRetrieval;

/// <summary>
/// T044 (US2) — Contract for GET /student/study/subjects.
///
/// The lesson-viewer retrieval contract requires the subjects list
/// response to carry the active session id, one entry per subject, and
/// bilingual display names plus chapter count and plan-gate state.
/// </summary>
public class SubjectsContractTests
{
    [Fact]
    public void SubjectsListResponse_Shape_Matches_Contract()
    {
        var props = typeof(SubjectsListResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("SessionId", props);
        Assert.Contains("Subjects", props);
    }

    [Fact]
    public void SubjectListItem_Carries_Bilingual_Display_Names_And_Plan_Gate()
    {
        var props = typeof(SubjectListItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("SubjectId", props);
        Assert.Contains("DisplayNameAr", props);
        Assert.Contains("DisplayNameEn", props);
        Assert.Contains("ChapterCount", props);
        Assert.Contains("PlanGate", props);
    }

    [Fact]
    public void Subject_Guid_Mapping_Is_Stable_And_Disjoint()
    {
        var ids = new[]
        {
            LessonRetrievalService.SubjectToGuid(Muallimi.Domain.Shared.Subject.Mathematics),
            LessonRetrievalService.SubjectToGuid(Muallimi.Domain.Shared.Subject.Science),
            LessonRetrievalService.SubjectToGuid(Muallimi.Domain.Shared.Subject.ArabicLanguage),
            LessonRetrievalService.SubjectToGuid(Muallimi.Domain.Shared.Subject.EnglishLanguage),
        };
        Assert.Equal(4, ids.Distinct().Count());
        foreach (var id in ids) Assert.NotEqual(Guid.Empty, id);

        var roundTrip = LessonRetrievalService.SubjectFromGuid(ids[0]);
        Assert.Equal(Muallimi.Domain.Shared.Subject.Mathematics, roundTrip);
    }
}
