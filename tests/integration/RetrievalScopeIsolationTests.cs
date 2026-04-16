using Muallimi.Api.RetrievalApi;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T092 - Integration test for cross-curriculum isolation.
/// Validates that retrieval never returns data from a different curriculum scope.
/// Cross-scope leakage is a contract failure.
/// </summary>
public class RetrievalScopeIsolationTests
{
    // ── Curriculum Type Isolation ──

    [Theory]
    [InlineData("Moe", "LanguageSchool")]
    [InlineData("Moe", "International")]
    [InlineData("LanguageSchool", "Moe")]
    [InlineData("LanguageSchool", "International")]
    [InlineData("International", "Moe")]
    [InlineData("International", "LanguageSchool")]
    public void CrossCurriculumType_Is_Rejected(string chunkType, string requestType)
    {
        var chunk = CreateChunkWithScope(chunkType, "Grade7", "Mathematics");
        var requestCt = Enum.Parse<CurriculumType>(requestType, ignoreCase: true);

        Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertChunksInScope(
                new List<ChunkResult> { chunk },
                requestCt, Grade.Grade7, Subject.Mathematics));
    }

    // ── Subject Isolation ──

    [Theory]
    [InlineData("Mathematics", "Science")]
    [InlineData("Science", "ArabicLanguage")]
    [InlineData("ArabicLanguage", "EnglishLanguage")]
    [InlineData("EnglishLanguage", "Mathematics")]
    public void CrossSubject_Is_Rejected(string chunkSubject, string requestSubject)
    {
        var chunk = CreateChunkWithScope("Moe", "Grade7", chunkSubject);
        var requestSub = Enum.Parse<Subject>(requestSubject, ignoreCase: true);

        Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertChunksInScope(
                new List<ChunkResult> { chunk },
                CurriculumType.Moe, Grade.Grade7, requestSub));
    }

    // ── Same Scope Passes ──

    [Theory]
    [InlineData("Moe", "Grade7", "Mathematics")]
    [InlineData("LanguageSchool", "Grade7", "Science")]
    [InlineData("International", "Grade7", "ArabicLanguage")]
    [InlineData("Moe", "Grade7", "EnglishLanguage")]
    public void SameScope_Passes(string ct, string grade, string subject)
    {
        var chunk = CreateChunkWithScope(ct, grade, subject);
        var ctEnum = Enum.Parse<CurriculumType>(ct, ignoreCase: true);
        var grEnum = Enum.Parse<Grade>(grade, ignoreCase: true);
        var subEnum = Enum.Parse<Subject>(subject, ignoreCase: true);

        // Should not throw
        ScopeIsolationFilter.AssertChunksInScope(
            new List<ChunkResult> { chunk }, ctEnum, grEnum, subEnum);
    }

    // ── Lesson-Level Asset Isolation ──

    [Fact]
    public void Visual_From_Different_Lesson_Is_Rejected()
    {
        var lessonA = Guid.NewGuid();
        var lessonB = Guid.NewGuid();

        Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertAssetsInScope(
                new[] { (assetLessonId: lessonA, requestedLessonId: lessonB) }));
    }

    [Fact]
    public void Visual_From_Same_Lesson_Passes()
    {
        var lesson = Guid.NewGuid();

        ScopeIsolationFilter.AssertAssetsInScope(
            new[] { (assetLessonId: lesson, requestedLessonId: lesson) });
    }

    [Fact]
    public void Multiple_Chunks_All_Must_Be_InScope()
    {
        var inScope = CreateChunkWithScope("Moe", "Grade7", "Mathematics");
        var outScope = CreateChunkWithScope("International", "Grade7", "Mathematics");

        // One out-of-scope chunk invalidates the entire result set
        Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertChunksInScope(
                new List<ChunkResult> { inScope, outScope },
                CurriculumType.Moe, Grade.Grade7, Subject.Mathematics));
    }

    [Fact]
    public void Empty_ChunkSet_Passes_Scope_Check()
    {
        // Edge case: no chunks returned — should not throw
        ScopeIsolationFilter.AssertChunksInScope(
            new List<ChunkResult>(),
            CurriculumType.Moe, Grade.Grade7, Subject.Mathematics);
    }

    [Fact]
    public void Empty_AssetSet_Passes_Scope_Check()
    {
        // Edge case: no assets returned (empty visual set fallback)
        ScopeIsolationFilter.AssertAssetsInScope(
            Array.Empty<(Guid, Guid)>());
    }

    // ── Helpers ──

    private static ChunkResult CreateChunkWithScope(string curriculumType, string grade, string subject)
    {
        return new ChunkResult
        {
            ChunkId = Guid.NewGuid(),
            Text = "Test chunk content",
            LessonId = Guid.NewGuid(),
            Metadata = $@"{{""curriculum_type"":""{curriculumType}"",""grade"":""{grade}"",""subject"":""{subject}""}}",
            Confidence = 0.95
        };
    }
}
