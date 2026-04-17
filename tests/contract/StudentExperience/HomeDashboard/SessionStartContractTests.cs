using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.PlanGating;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.HomeDashboard;

/// <summary>
/// T026 (US1) — Contract for POST /student/session/start.
///
/// The contract requires the response to:
///   - echo the inbound correlation_id as a GUID,
///   - carry tutor_language (ar | en), plan_tier_snapshot, curriculum_type,
///     grade from the persisted StudentSession,
///   - include mode_tile_states for all seven downstream modes with
///     per-mode enabled + reason + plan_gate + subject_gate,
///   - include Arabic and English greeting strings with MSA quality.
/// </summary>
public class SessionStartContractTests
{
    [Fact]
    public void SessionStartResponse_Shape_Matches_Contract()
    {
        var props = typeof(SessionStartResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("CorrelationId", props);
        Assert.Contains("TutorLanguage", props);
        Assert.Contains("CurriculumType", props);
        Assert.Contains("Grade", props);
        Assert.Contains("PlanTierSnapshot", props);
        Assert.Contains("ModeTileStates", props);
        Assert.Contains("ResumeTarget", props);
        Assert.Contains("RecommendedTopics", props);
        Assert.Contains("GreetingTextAr", props);
        Assert.Contains("GreetingTextEn", props);
    }

    [Fact]
    public void ModeTileState_Carries_Contract_Fields()
    {
        var props = typeof(ModeTileState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("Mode", props);
        Assert.Contains("Enabled", props);
        Assert.Contains("Reason", props);
        Assert.Contains("PlanGate", props);
        Assert.Contains("SubjectGate", props);
    }

    [Fact]
    public void RecommendedTopic_Carries_Bilingual_Display_Names()
    {
        var props = typeof(RecommendedTopic)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
        Assert.Contains("TopicId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("DisplayNameAr", props);
        Assert.Contains("DisplayNameEn", props);
    }

    [Fact]
    public async Task ResolveTiles_Returns_All_Seven_Modes_With_Required_Fields()
    {
        var service = BuildServiceWithPlanGate(new AllowAllPlanGate());
        var tiles = await service.ResolveTilesAsync(Guid.NewGuid(), "premium");

        Assert.Equal(7, tiles.Count);
        var modes = tiles.Select(t => t.Mode).ToHashSet();
        Assert.Contains(StudentModes.Study, modes);
        Assert.Contains(StudentModes.TutorChat, modes);
        Assert.Contains(StudentModes.TutorVoice, modes);
        Assert.Contains(StudentModes.SolveQuestions, modes);
        Assert.Contains(StudentModes.MockTest, modes);
        Assert.Contains(StudentModes.HomeworkHelp, modes);
        Assert.Contains(StudentModes.Whiteboard, modes);

        foreach (var tile in tiles)
        {
            Assert.False(string.IsNullOrEmpty(tile.PlanGate));
            Assert.False(string.IsNullOrEmpty(tile.SubjectGate));
            Assert.True(tile.Enabled);
            Assert.Equal("open", tile.PlanGate);
        }
    }

    [Fact]
    public async Task ResolveTiles_FailsClosed_When_PlanGate_Denies()
    {
        var service = BuildServiceWithPlanGate(new DenyAllPlanGate("plan_tier_not_permitted"));
        var tiles = await service.ResolveTilesAsync(Guid.NewGuid(), "free");

        foreach (var tile in tiles)
        {
            Assert.False(tile.Enabled);
            Assert.Equal("closed", tile.PlanGate);
            Assert.Equal("plan_tier_not_permitted", tile.Reason);
        }
    }

    [Fact]
    public void Mode_Transition_To_Event_Kind_Is_Disjoint_Per_Mode()
    {
        // Ensures the quiz_answered vs mock_test distinction required by
        // T102 (SessionEvent label disjointness).
        Assert.NotEqual(
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.SolveQuestions),
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.MockTest));
        Assert.Equal(SessionEventKind.quiz_answered,
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.SolveQuestions));
        Assert.Equal(SessionEventKind.mock_test,
            SessionModeEndpoint.MapTargetModeToEventKind(StudentModes.MockTest));
    }

    [Fact]
    public void ResolveCorrelationId_Prefers_Items_Then_Header_Then_NewGuid()
    {
        // Pure-logic check: the helper never returns Guid.Empty and prefers
        // HttpContext.Items over the header.
        var ctxA = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctxA.Items["CorrelationId"] = "4a1f5b1e-3a2b-4a3c-8b4d-5a6f7b8c9d01";
        ctxA.Request.Headers["X-Correlation-Id"] = "deadbeef-dead-beef-dead-beefdeadbeef";
        var a = SessionStartEndpoint.ResolveCorrelationId(ctxA);
        Assert.Equal(Guid.Parse("4a1f5b1e-3a2b-4a3c-8b4d-5a6f7b8c9d01"), a);

        var ctxB = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctxB.Request.Headers["X-Correlation-Id"] = "4a1f5b1e-3a2b-4a3c-8b4d-5a6f7b8c9d02";
        var b = SessionStartEndpoint.ResolveCorrelationId(ctxB);
        Assert.Equal(Guid.Parse("4a1f5b1e-3a2b-4a3c-8b4d-5a6f7b8c9d02"), b);

        var ctxC = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var c = SessionStartEndpoint.ResolveCorrelationId(ctxC);
        Assert.NotEqual(Guid.Empty, c);
    }

    // ── helpers ──

    private static HomeDashboardService BuildServiceWithPlanGate(IPlanGateResolver planGate)
    {
        // The service's DB is only consulted for resume-target lookups, which
        // are covered in the endpoint-level integration tests. ResolveTilesAsync
        // takes no DB path, so passing null here is safe for these shape tests.
        return new HomeDashboardService(planGate, db: null!);
    }
}

internal sealed class AllowAllPlanGate : IPlanGateResolver
{
    public Task<PlanGateDecision> EvaluateAsync(PlanGateContext context, CancellationToken ct = default)
        => Task.FromResult(new PlanGateDecision(true, null, null));
}

internal sealed class DenyAllPlanGate : IPlanGateResolver
{
    private readonly string _reason;
    public DenyAllPlanGate(string reason) { _reason = reason; }
    public Task<PlanGateDecision> EvaluateAsync(PlanGateContext context, CancellationToken ct = default)
        => Task.FromResult(new PlanGateDecision(false, _reason, null));
}
