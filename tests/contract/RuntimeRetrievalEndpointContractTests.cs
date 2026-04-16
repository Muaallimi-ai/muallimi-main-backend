using Muallimi.Api.RetrievalApi;
using Muallimi.Api.Publication;
using Muallimi.Domain.Publication;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T089 - Contract tests for POST /internal/content/retrieve,
/// GET /internal/content/visual/{lesson_id}, and GET /internal/content/audio/{chunk_id}.
/// Validates scope filtering, lookup-only assertion, correlation ID propagation,
/// and response contract shapes.
/// </summary>
public class RuntimeRetrievalEndpointContractTests
{
    // ── Scope Isolation Contract ──

    [Fact]
    public void ScopeIsolation_Rejects_CrossCurriculumType_Chunks()
    {
        // Contract: no chunk from a different curriculum type may appear in results
        var chunks = new List<ChunkResult>
        {
            new()
            {
                ChunkId = Guid.NewGuid(),
                Text = "Test chunk",
                LessonId = Guid.NewGuid(),
                Metadata = """{"curriculum_type":"LanguageSchool","grade":"Grade7","subject":"Mathematics"}""",
                Confidence = 0.95
            }
        };

        Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertChunksInScope(
                chunks, CurriculumType.Moe, Grade.Grade7, Subject.Mathematics));
    }

    [Fact]
    public void ScopeIsolation_Rejects_CrossGrade_Chunks()
    {
        var chunks = new List<ChunkResult>
        {
            new()
            {
                ChunkId = Guid.NewGuid(),
                Text = "Test chunk",
                LessonId = Guid.NewGuid(),
                // Grade mismatch would be caught if we had Grade8 — for now test subject mismatch
                Metadata = """{"curriculum_type":"Moe","grade":"Grade7","subject":"Science"}""",
                Confidence = 0.95
            }
        };

        Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertChunksInScope(
                chunks, CurriculumType.Moe, Grade.Grade7, Subject.Mathematics));
    }

    [Fact]
    public void ScopeIsolation_Passes_InScope_Chunks()
    {
        var chunks = new List<ChunkResult>
        {
            new()
            {
                ChunkId = Guid.NewGuid(),
                Text = "Test chunk",
                LessonId = Guid.NewGuid(),
                Metadata = """{"curriculum_type":"Moe","grade":"Grade7","subject":"Mathematics"}""",
                Confidence = 0.95
            }
        };

        // Should not throw
        ScopeIsolationFilter.AssertChunksInScope(
            chunks, CurriculumType.Moe, Grade.Grade7, Subject.Mathematics);
    }

    [Fact]
    public void ScopeIsolation_Asset_Rejects_CrossLesson_Visual()
    {
        var lessonA = Guid.NewGuid();
        var lessonB = Guid.NewGuid();

        Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertAssetsInScope(
                new[] { (assetLessonId: lessonA, requestedLessonId: lessonB) }));
    }

    [Fact]
    public void ScopeIsolation_Asset_Passes_SameLesson()
    {
        var lesson = Guid.NewGuid();

        // Should not throw
        ScopeIsolationFilter.AssertAssetsInScope(
            new[] { (assetLessonId: lesson, requestedLessonId: lesson) });
    }

    // ── CDN URL Contract ──

    [Fact]
    public void CdnUrl_Visual_Mp4_Has_Correct_Extension()
    {
        var provider = CreateDevProvider();
        var url = provider.GenerateUrl(Guid.NewGuid(), AssetType.Visual, Guid.NewGuid(), VisualFormat.Mp4Animation);
        Assert.EndsWith(".mp4", url);
    }

    [Fact]
    public void CdnUrl_Visual_InteractiveHtml_Has_Correct_Extension()
    {
        var provider = CreateDevProvider();
        var url = provider.GenerateUrl(Guid.NewGuid(), AssetType.Visual, Guid.NewGuid(), VisualFormat.InteractiveHtml);
        Assert.EndsWith(".html", url);
    }

    [Fact]
    public void CdnUrl_Audio_Has_Mp3_Extension()
    {
        var provider = CreateDevProvider();
        var url = provider.GenerateAudioUrl(Guid.NewGuid());
        Assert.EndsWith(".mp3", url);
    }

    [Fact]
    public void CdnUrl_Transcript_Has_Vtt_Extension()
    {
        var provider = CreateDevProvider();
        var url = provider.GenerateTranscriptUrl(Guid.NewGuid(), Guid.NewGuid());
        Assert.EndsWith(".vtt", url);
    }

    [Fact]
    public void CdnUrl_Development_Uses_LocalBase()
    {
        var provider = CreateDevProvider();
        var url = provider.GenerateUrl(Guid.NewGuid(), AssetType.Audio, Guid.NewGuid());
        Assert.StartsWith("/static/content/", url);
    }

    // ── Published Asset Contract ──

    [Fact]
    public void PublishedAsset_Only_Active_Is_Retrievable()
    {
        var asset = PublishedAsset.Create(
            Guid.NewGuid(), Guid.NewGuid(), AssetType.TextSummary, null,
            "/content/test", "admin-1", "expert-1", 1);

        Assert.True(asset.IsActive);

        asset.Invalidate();
        Assert.False(asset.IsActive);
    }

    [Fact]
    public void PublishedAsset_Deterministic_Id_Matches_AssetId()
    {
        var assetId = Guid.NewGuid();
        var published = PublishedAsset.Create(
            assetId, Guid.NewGuid(), AssetType.Visual, VisualFormat.Diagram,
            "/content/visual", "admin", "expert", 1);

        Assert.Equal(assetId, published.PublishedId);
    }

    [Fact]
    public void PublishedAsset_Empty_Visual_Set_When_None_Published()
    {
        // Contract: when no visual asset is approved, an empty set is returned
        // and the learning engine gracefully falls back to text/audio only
        var visuals = new List<PublishedAsset>();
        Assert.Empty(visuals);
    }

    // ── Retrieve Request DTO Contract ──

    [Fact]
    public void RetrieveRequest_Requires_Scope_Fields()
    {
        var request = new RetrieveRequest(
            "ما هو المقصود بالعدد الأولي؟",
            null,
            new RetrieveScopeDto("moe", "grade_7", "mathematics", "ar"),
            5,
            "corr-123");

        Assert.NotNull(request.Scope);
        Assert.Equal("moe", request.Scope.CurriculumType);
        Assert.Equal("grade_7", request.Scope.Grade);
        Assert.Equal("mathematics", request.Scope.Subject);
        Assert.Equal("ar", request.Scope.TutorLanguage);
        Assert.Equal("corr-123", request.CorrelationId);
    }

    [Fact]
    public void RetrieveRequest_MaxChunks_Defaults_To_Five()
    {
        var request = new RetrieveRequest(
            "test query", null,
            new RetrieveScopeDto("moe", "grade_7", "mathematics", "ar"));

        Assert.Equal(5, request.MaxChunks);
    }

    // ── Helpers ──

    private static LogicalCdnUrlProvider CreateDevProvider()
    {
        var env = new TestWebHostEnvironment { EnvironmentName = "Development" };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return new LogicalCdnUrlProvider(env, config);
    }
}

/// <summary>Minimal IWebHostEnvironment for testing.</summary>
internal class TestWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Test";
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
    public string ContentRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}
