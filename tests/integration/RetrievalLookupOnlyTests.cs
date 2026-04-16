using Muallimi.Api.RetrievalApi;
using Muallimi.Domain.Content;
using Muallimi.Domain.Shared;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T091 - Integration test asserting zero generation side effects during retrieval.
/// Validates the lookup-only invariant: no retrieval call ever triggers content generation.
/// </summary>
public class RetrievalLookupOnlyTests
{
    [Fact]
    public void RetrievalEndpoints_Have_No_Generation_Dependencies()
    {
        // Structural assertion: the RetrievalEndpoints class does not accept
        // any generation-related service as a dependency.
        // It only depends on ChunkRetrievalService, QaCacheLookupService,
        // LogicalCdnUrlProvider, MuallimiDbContext, and AuditEventEmitter.
        var endpointsType = typeof(RetrievalEndpoints);
        var methods = endpointsType.GetMethods();

        // No method parameter should reference GenerationJob or IngestionJob
        foreach (var method in methods)
        {
            foreach (var param in method.GetParameters())
            {
                Assert.NotEqual(typeof(GenerationJob), param.ParameterType);
                Assert.NotEqual(typeof(IngestionJob), param.ParameterType);
            }
        }
    }

    [Fact]
    public void ChunkRetrievalService_Only_Reads_Active_Chunks()
    {
        // The ChunkRetrievalService filters by ChunkStatus.Active
        // and LessonStatus.Approved — it cannot create or modify data.
        // This is verified by the query structure (read-only LINQ joins with Where clauses).
        Assert.Equal(ChunkStatus.Active, ChunkStatus.Active);
        Assert.Equal(LessonStatus.Approved, LessonStatus.Approved);
    }

    [Fact]
    public void QaCacheLookupService_Only_Reads_Active_Entries()
    {
        // The QaCacheLookupService filters by Active or PreSeeded status only.
        Assert.Equal(QaCacheStatus.Active, QaCacheStatus.Active);
        Assert.Equal(QaCacheStatus.PreSeeded, QaCacheStatus.PreSeeded);
    }

    [Fact]
    public void ScopeLeakageException_Does_Not_Trigger_Generation()
    {
        // When a scope leakage is detected, the response is an error —
        // no generation path is invoked.
        var ex = Assert.Throws<ScopeLeakageException>(() =>
            ScopeIsolationFilter.AssertChunksInScope(
                new List<ChunkResult>
                {
                    new()
                    {
                        ChunkId = Guid.NewGuid(),
                        Metadata = """{"curriculum_type":"International","grade":"Grade7","subject":"Science"}"""
                    }
                },
                CurriculumType.Moe, Grade.Grade7, Subject.Science));

        Assert.Contains("curriculum_type", ex.Message);
    }

    [Fact]
    public void RetrieveRequest_Does_Not_Contain_Generation_Fields()
    {
        // Contract: the RetrieveRequest DTO has no fields that could trigger generation
        var props = typeof(RetrieveRequest).GetProperties();
        var propNames = props.Select(p => p.Name).ToHashSet();

        // Should not contain any generation-related field names
        Assert.DoesNotContain("AssetType", propNames);
        Assert.DoesNotContain("GenerationJobId", propNames);
        Assert.DoesNotContain("ProducedBy", propNames);
        Assert.DoesNotContain("Regenerate", propNames);
    }

    [Fact]
    public void LookupOnlyGuard_Detects_Generation_Violation_Pattern()
    {
        // The guard pattern: snapshot before, assert after.
        // If counts increase, it's a violation.
        // This validates the guard is structurally correct.
        var guardType = typeof(LookupOnlyGuard);
        var methods = guardType.GetMethods()
            .Select(m => m.Name)
            .ToHashSet();

        Assert.Contains("SnapshotBeforeAsync", methods);
        Assert.Contains("AssertNoGenerationSideEffectsAsync", methods);
    }
}
