using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.LessonRetrieval;

/// <summary>
/// T045 (US2) — Contract for GET /student/study/subjects/{subject_id}/chapters.
///
/// Shape contract: response carries the originating subject_id plus a
/// list of chapters, each with bilingual names and a nested topics list
/// whose entries carry their own bilingual names and lesson_count.
/// </summary>
public class ChaptersContractTests
{
    [Fact]
    public void ChaptersListResponse_Shape_Matches_Contract()
    {
        var props = typeof(ChaptersListResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("SubjectId", props);
        Assert.Contains("Chapters", props);
    }

    [Fact]
    public void ChapterListItem_Carries_Bilingual_Names_And_Topics()
    {
        var props = typeof(ChapterListItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("ChapterId", props);
        Assert.Contains("DisplayNameAr", props);
        Assert.Contains("DisplayNameEn", props);
        Assert.Contains("Topics", props);
    }

    [Fact]
    public void TopicListItem_Carries_Bilingual_Names_And_Lesson_Count()
    {
        var props = typeof(TopicListItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("TopicId", props);
        Assert.Contains("DisplayNameAr", props);
        Assert.Contains("DisplayNameEn", props);
        Assert.Contains("LessonCount", props);
    }
}
