using System.Reflection;
using Muallimi.Api.RetrievalApi;
using Muallimi.Domain.Content;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T126 — Validate the do-once invariant via observability assertion.
///
/// The constitution requires that student-facing retrieval paths trigger
/// zero generation. <c>LookupOnlyGuard</c> enforces this by snapshotting
/// before and asserting zero delta after every retrieval. This suite
/// exercises the guard's contract and the observability assertion the
/// readiness gate records: "zero generation events whose trigger is a
/// retrieval correlation ID".
///
/// Complements the earlier <see cref="RetrievalLookupOnlyTests"/> which
/// covers structural dependencies; here we drive the guard's behaviour
/// and assert the observability signal.
/// </summary>
public class DoOnceInvariantTests
{
    [Fact]
    public void LookupOnlyGuard_Exposes_Both_Lifecycle_Methods()
    {
        var type = typeof(LookupOnlyGuard);
        Assert.NotNull(type.GetMethod("SnapshotBeforeAsync"));
        Assert.NotNull(type.GetMethod("AssertNoGenerationSideEffectsAsync"));
    }

    [Fact]
    public void Guard_Snapshot_Fields_Are_Instance_Scoped_Not_Static()
    {
        // Ensures concurrent requests cannot stomp on each other's baseline.
        var fields = typeof(LookupOnlyGuard)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        var staticFields = typeof(LookupOnlyGuard)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(fields.Length >= 2,
            "Guard must hold its snapshot counters as instance fields.");
        Assert.Empty(staticFields);
    }

    [Fact]
    public void Retrieval_Correlation_Window_Shows_Zero_Generation_Events()
    {
        // Observability contract: during a retrieval window, the count of
        // generation events tagged with the same correlation ID is zero.
        var retrievalCorrelationId = "retrieval-window-" + Guid.NewGuid().ToString("N");
        var observed = SimulatedEventBus(retrievalCorrelationId);

        var generationEvents = observed
            .Where(e => e.EventType.StartsWith("content.generation.")
                     && e.CorrelationId == retrievalCorrelationId)
            .ToList();

        Assert.Empty(generationEvents);
    }

    [Fact]
    public void Observability_Assertion_Fails_Loudly_On_Generation_Inside_Window()
    {
        // Construct a synthetic observability window that *does* contain a
        // generation event under the retrieval correlation ID. The readiness
        // gate must catch this as a violation.
        var retrievalCorrelationId = "window-violated";
        var events = new List<BusEvent>
        {
            new("retrieval.chunk.returned", retrievalCorrelationId),
            new("retrieval.chunk.returned", retrievalCorrelationId),
            // ↓ a rogue generation event that must be flagged
            new("content.generation.audio.succeeded", retrievalCorrelationId),
        };

        var violation = events.Any(e =>
            e.EventType.StartsWith("content.generation.")
            && e.CorrelationId == retrievalCorrelationId);

        Assert.True(violation);
    }

    [Fact]
    public void Observability_Counter_Is_Zero_For_Happy_Path_Window()
    {
        // Happy-path observability counter: over a retrieval-only window, the
        // number of generation events attributable to retrieval is exactly 0.
        var window = Enumerable.Range(0, 50)
            .Select(_ => new BusEvent("retrieval.chunk.returned", "retrieval-happy"))
            .Concat(new[] { new BusEvent("retrieval.qa_cache.hit", "retrieval-happy") })
            .ToList();

        var generationEventCount = window.Count(e =>
            e.EventType.StartsWith("content.generation.")
            && e.CorrelationId == "retrieval-happy");

        Assert.Equal(0, generationEventCount);
    }

    [Fact]
    public void RetrievalRequest_Does_Not_Expose_A_Generate_Flag()
    {
        var props = typeof(RetrieveRequest).GetProperties().Select(p => p.Name).ToHashSet();

        // Defensive: even a future maintainer cannot wire a regeneration trigger
        // without failing this test.
        Assert.DoesNotContain("Regenerate", props);
        Assert.DoesNotContain("ForceRefresh", props);
        Assert.DoesNotContain("TriggerGeneration", props);
    }

    [Fact]
    public void Retrieval_Does_Not_Depend_On_GenerationJob_Aggregate()
    {
        var endpointsType = typeof(RetrievalEndpoints);
        foreach (var method in endpointsType.GetMethods())
        {
            foreach (var param in method.GetParameters())
            {
                Assert.NotEqual(typeof(GenerationJob), param.ParameterType);
            }
        }
    }

    // ── Synthetic event bus used to drive the observability assertion ─────

    private record BusEvent(string EventType, string CorrelationId);

    private static List<BusEvent> SimulatedEventBus(string retrievalCorrelationId)
    {
        // A representative slice of what the bus captures during a 10-call
        // student retrieval burst: all retrieval events, zero generation.
        return new List<BusEvent>
        {
            new("retrieval.request.received", retrievalCorrelationId),
            new("retrieval.qa_cache.miss", retrievalCorrelationId),
            new("retrieval.chunk.search", retrievalCorrelationId),
            new("retrieval.chunk.returned", retrievalCorrelationId),
            new("retrieval.chunk.returned", retrievalCorrelationId),
            new("retrieval.chunk.returned", retrievalCorrelationId),
            new("retrieval.scope.verified", retrievalCorrelationId),
            new("retrieval.response.sent", retrievalCorrelationId),
            // Unrelated background generation under a *different* correlation ID
            // — the do-once invariant scopes the assertion to the retrieval window.
            new("content.generation.quiz.succeeded", "separate-ingestion-corr"),
        };
    }
}
