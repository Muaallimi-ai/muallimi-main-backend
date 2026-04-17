using System;
using System.Linq;
using System.Reflection;
using Muallimi.Api.StudentExperience.Whiteboard;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.Whiteboard;

/// <summary>
/// T115 (US8) — Contract tests for
/// <c>POST /student/whiteboard/start</c>,
/// <c>POST /student/whiteboard/{id}/step</c>, and
/// <c>POST /student/whiteboard/{id}/end</c>.
///
/// Shapes mirror
/// <c>specs/005-student-learning-experience/contracts/whiteboard-session-contract.md</c>.
/// The catalogue entry lives in
/// <c>src/Muallimi.Api/StudentExperience/Contracts/Phase3ContractCatalogue.cs</c>
/// under <c>student.whiteboard</c>.
///
/// These assertions also cover constitution invariants that are visible from
/// the wire:
///   - Refusal vocabulary is restricted to <c>plan_gate</c>,
///     <c>subject_gate</c>, <c>tenant_denied</c>, and <c>scope_not_found</c>.
///   - Narration voice profile source is explicit so Phase 1 teacher voices
///     and Phase 2 AI tutor voices remain distinct on the playback wire.
///   - <c>whiteboard_session</c> is one of the eleven session-event kinds.
/// </summary>
public class WhiteboardContractTests
{
    [Fact]
    public void WhiteboardStartRequest_Carries_Session_Subject_Topic_And_Mode()
    {
        var props = typeof(WhiteboardStartRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("SessionMode", props);
    }

    [Fact]
    public void WhiteboardStartResponse_Accepted_Shape_Matches_Contract()
    {
        var props = typeof(WhiteboardStartResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("WhiteboardSessionId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("PlanTierSnapshot", props);
        Assert.Contains("StartedAt", props);
        Assert.Contains("InitialCanvasState", props);
        Assert.Contains("RefusalReason", props);
        Assert.Contains("RefusalTextAr", props);
        Assert.Contains("RefusalTextEn", props);
    }

    [Fact]
    public void WhiteboardStepRequest_Shape_Matches_Contract()
    {
        var props = typeof(WhiteboardStepRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("WhiteboardSessionId", props);
        Assert.Contains("RequestedStepIndex", props);
    }

    [Fact]
    public void WhiteboardStepResponse_Carries_Draw_Ops_Narration_And_Evidence()
    {
        var props = typeof(WhiteboardStepResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("WhiteboardSessionId", props);
        Assert.Contains("StepIndex", props);
        Assert.Contains("DrawOps", props);
        Assert.Contains("NarrationTextAr", props);
        Assert.Contains("NarrationTextEn", props);
        Assert.Contains("NarrationVoiceProfileId", props);
        Assert.Contains("NarrationVoiceProfileSource", props);
        Assert.Contains("EvidenceRefs", props);
    }

    [Fact]
    public void WhiteboardDrawOp_Shape_Matches_Contract()
    {
        var props = typeof(WhiteboardDrawOp)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("OpType", props);
        Assert.Contains("Payload", props);
    }

    [Fact]
    public void WhiteboardEvidenceRef_Shape_Matches_Contract()
    {
        var props = typeof(WhiteboardEvidenceRef)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("ChunkId", props);
        Assert.Contains("SourceUri", props);
    }

    [Fact]
    public void WhiteboardEndRequest_Shape_Matches_Contract()
    {
        var props = typeof(WhiteboardEndRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("WhiteboardSessionId", props);
        Assert.Contains("EndReason", props);
    }

    [Fact]
    public void Refusal_Vocabulary_Matches_Contract()
    {
        Assert.Equal("plan_gate", WhiteboardRefusalReasons.PlanGate);
        Assert.Equal("subject_gate", WhiteboardRefusalReasons.SubjectGate);
        Assert.Equal("tenant_denied", WhiteboardRefusalReasons.TenantDenied);
        Assert.Equal("scope_not_found", WhiteboardRefusalReasons.ScopeNotFound);
    }

    [Fact]
    public void Session_Modes_Cover_Step_Through_And_Free_Draw_Gated()
    {
        Assert.True(WhiteboardSessionModes.IsAccepted("step_through"));
        Assert.True(WhiteboardSessionModes.IsAccepted("free_draw_gated"));
        Assert.False(WhiteboardSessionModes.IsAccepted("free_draw"));
        Assert.False(WhiteboardSessionModes.IsAccepted(""));
        Assert.False(WhiteboardSessionModes.IsAccepted(null));
    }

    [Fact]
    public void End_Reasons_Cover_Contract_Values()
    {
        Assert.True(WhiteboardEndReasons.IsAccepted("student_ended"));
        Assert.True(WhiteboardEndReasons.IsAccepted("timeout"));
        Assert.True(WhiteboardEndReasons.IsAccepted("gate_revoked"));
        Assert.False(WhiteboardEndReasons.IsAccepted("signed_out"));
    }

    [Fact]
    public void Narration_Voice_Profile_Sources_Remain_Distinct()
    {
        Assert.Equal("phase1_curriculum", WhiteboardNarrationVoiceProfileSources.Phase1Curriculum);
        Assert.Equal("phase2_ai_tutor", WhiteboardNarrationVoiceProfileSources.Phase2AiTutor);
    }

    [Fact]
    public void Whiteboard_Session_Event_Kind_Exists_In_SessionEventKind_Enum()
    {
        var kinds = Enum.GetNames<Muallimi.Api.StudentExperience.SessionEvents.SessionEventKind>();
        Assert.Contains("whiteboard_session", kinds);
    }

    [Fact]
    public void Whiteboard_Routes_Are_Registered_On_The_Endpoints_Surface()
    {
        Assert.Equal("/api/student/whiteboard/start", WhiteboardEndpoints.StartRoute);
        Assert.Equal("/api/student/whiteboard/{id:guid}/step", WhiteboardEndpoints.StepRoute);
        Assert.Equal("/api/student/whiteboard/{id:guid}/end", WhiteboardEndpoints.EndRoute);
    }

    [Fact]
    public void Whiteboard_Endpoints_Are_Catalogued()
    {
        var entry = Muallimi.Api.StudentExperience.Contracts.Phase3ContractCatalogue.All
            .Single(c => c.ContractId == "student.whiteboard");
        var paths = entry.Endpoints.Select(e => e.Path).ToList();
        Assert.Contains("/student/whiteboard/start", paths);
        Assert.Contains("/student/whiteboard/step", paths);
        Assert.Contains("/student/whiteboard/end", paths);
    }
}
