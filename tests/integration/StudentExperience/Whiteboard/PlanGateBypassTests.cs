using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Api.StudentExperience.PlanGating;
using Muallimi.Api.StudentExperience.Whiteboard;
using Muallimi.Domain.Shared;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.StudentExperience.Whiteboard;

/// <summary>
/// T123 (US8) — Plan gate bypass red-team.
///
/// The whiteboard surface is plan-gated AND subject-gated. The UI gate is
/// advisory only; the constitution rule is that every gated entry point
/// MUST re-check the plan and subject on the backend. This test simulates
/// a caller who bypasses the UI and calls the facade directly with a free
/// plan or with a non-eligible subject, and asserts that the service
/// refuses with the contract-vocabulary reason — never creating a
/// <see cref="WhiteboardSession"/> row, never loading any Phase 1
/// approved content.
///
/// Scenarios covered:
///   - Free plan + Mathematics: refused with <c>plan_gate</c>.
///   - Premium plan + Arabic language: refused with <c>subject_gate</c>.
///   - Premium plan + Mathematics + gate revoked mid-session: step call
///     returns <c>GateRevoked</c> so the endpoint can end the session with
///     <c>gate_revoked</c>.
/// </summary>
public class PlanGateBypassTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000201");
    private static readonly Guid SessionId = Guid.Parse("00000000-0000-0000-0000-000000000202");
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000203");
    private static readonly Guid CorrelationId = Guid.Parse("00000000-0000-0000-0000-000000000204");

    private static (Muallimi.Domain.StudentExperience.StudentSession Session, StudentProfile Profile) MakeSessionAndProfile(
        string planTier, string? tutorLanguage = "ar")
    {
        var session = new Muallimi.Domain.StudentExperience.StudentSession
        {
            Id = SessionId,
            TenantId = TenantId,
            StudentProfileId = ProfileId,
            CorrelationId = CorrelationId,
            ActiveMode = "whiteboard",
            TutorLanguage = tutorLanguage ?? "ar",
            PlanTierSnapshot = planTier,
        };
        var profile = new StudentProfile
        {
            Id = ProfileId,
            TenantId = TenantId,
            CurriculumType = "moe",
            Grade = "grade_7",
            PreferredLanguage = "ar",
            PlanTier = planTier,
        };
        return (session, profile);
    }

    private static MuallimiDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<MuallimiDbContext>()
            .UseInMemoryDatabase($"wb-bypass-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MuallimiDbContext(options);
    }

    [Fact]
    public async Task Free_Plan_Is_Refused_With_PlanGate_Reason_Even_On_Direct_Backend_Call()
    {
        var (session, profile) = MakeSessionAndProfile(planTier: "free");
        await using var db = NewDbContext();
        var planGate = new StubPlanGateResolver(new PlanGateDecision(
            Allowed: false, Reason: "plan_tier_not_permitted", AppliedPolicy: null));
        var repo = new InMemoryWhiteboardRepository();
        var service = new WhiteboardService(db, planGate, repo);

        var request = new WhiteboardStartRequest(
            SessionId: session.Id,
            SubjectId: LessonRetrievalService.SubjectToGuid(Subject.Mathematics),
            TopicId: Guid.NewGuid(),
            SessionMode: WhiteboardSessionModes.StepThrough);

        var result = await service.StartAsync(session, profile, request);

        Assert.Equal(WhiteboardStartOutcome.Refused, result.Outcome);
        Assert.Null(result.WhiteboardSession);
        Assert.NotNull(result.Response);
        Assert.Equal(WhiteboardRefusalReasons.PlanGate, result.Response!.RefusalReason);
        Assert.False(string.IsNullOrWhiteSpace(result.Response.RefusalTextAr));
        Assert.False(string.IsNullOrWhiteSpace(result.Response.RefusalTextEn));
        Assert.Empty(repo.AllSessions);
    }

    [Fact]
    public async Task Premium_Plan_On_Non_Eligible_Subject_Is_Refused_With_SubjectGate_Reason()
    {
        // Arabic language is a supported subject for other modes but is
        // explicitly outside the whiteboard MVP allow-list. Even on a
        // premium plan the backend MUST refuse at the subject gate before
        // touching the DB.
        var (session, profile) = MakeSessionAndProfile(planTier: "premium", tutorLanguage: "en");
        await using var db = NewDbContext();
        var planGate = new StubPlanGateResolver(new PlanGateDecision(
            Allowed: true, Reason: null, AppliedPolicy: null));
        var repo = new InMemoryWhiteboardRepository();
        var service = new WhiteboardService(db, planGate, repo);

        var request = new WhiteboardStartRequest(
            SessionId: session.Id,
            SubjectId: LessonRetrievalService.SubjectToGuid(Subject.ArabicLanguage),
            TopicId: Guid.NewGuid(),
            SessionMode: WhiteboardSessionModes.StepThrough);

        var result = await service.StartAsync(session, profile, request);

        Assert.Equal(WhiteboardStartOutcome.Refused, result.Outcome);
        Assert.Equal(WhiteboardRefusalReasons.SubjectGate, result.Response!.RefusalReason);
        Assert.Null(result.WhiteboardSession);
        Assert.Empty(repo.AllSessions);
    }

    [Fact]
    public async Task Gate_Revoked_Mid_Session_Returns_GateRevoked_On_Step()
    {
        // Simulates a premium plan that was downgraded while a whiteboard
        // session is active. The stored WhiteboardSession still carries a
        // premium plan_tier_snapshot, but the live student_session has
        // moved to free. The step MUST re-check the gate and surface
        // GateRevoked so the endpoint can end the session with
        // gate_revoked.
        var (session, profile) = MakeSessionAndProfile(planTier: "free");
        await using var db = NewDbContext();
        var planGate = new StubPlanGateResolver(new PlanGateDecision(
            Allowed: false, Reason: "plan_tier_not_permitted", AppliedPolicy: null));
        var repo = new InMemoryWhiteboardRepository();
        var service = new WhiteboardService(db, planGate, repo);

        var whiteboard = new WhiteboardSession
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            StudentSessionId = session.Id,
            SubjectId = LessonRetrievalService.SubjectToGuid(Subject.Mathematics),
            TopicId = Guid.NewGuid(),
            PlanTierSnapshot = "premium",
            SessionMode = WhiteboardSessionModes.StepThrough,
            StepLog = "[]",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        repo.AllSessions.Add(whiteboard);

        var stepRequest = new WhiteboardStepRequest(whiteboard.Id, RequestedStepIndex: 0);
        var result = await service.StepAsync(session, profile, whiteboard, stepRequest);

        Assert.Equal(WhiteboardStepOutcome.GateRevoked, result.Outcome);
        Assert.Null(result.Response);
        Assert.Equal("plan_tier_not_permitted", result.RevokedReason);
    }

    [Fact]
    public async Task Invalid_Subject_Guid_Is_Refused_With_ScopeNotFound()
    {
        var (session, profile) = MakeSessionAndProfile(planTier: "premium");
        await using var db = NewDbContext();
        var planGate = new StubPlanGateResolver(new PlanGateDecision(
            Allowed: true, Reason: null, AppliedPolicy: null));
        var repo = new InMemoryWhiteboardRepository();
        var service = new WhiteboardService(db, planGate, repo);

        var request = new WhiteboardStartRequest(
            SessionId: session.Id,
            SubjectId: Guid.NewGuid(), // not in LessonRetrievalService map
            TopicId: Guid.NewGuid(),
            SessionMode: WhiteboardSessionModes.StepThrough);

        var result = await service.StartAsync(session, profile, request);

        Assert.Equal(WhiteboardStartOutcome.Refused, result.Outcome);
        Assert.Equal(WhiteboardRefusalReasons.ScopeNotFound, result.Response!.RefusalReason);
    }

    [Fact]
    public void Service_Eligible_Subjects_Do_Not_Include_Arabic_Or_English_Language()
    {
        // Locks in the MVP allow-list so a future refactor can't silently
        // extend the whiteboard to non-STEM subjects without updating the
        // plan-gate policy + the e2e test matrix.
        Assert.Contains(Subject.Mathematics, WhiteboardService.EligibleSubjects);
        Assert.DoesNotContain(Subject.ArabicLanguage, WhiteboardService.EligibleSubjects);
        Assert.DoesNotContain(Subject.EnglishLanguage, WhiteboardService.EligibleSubjects);
    }

    internal sealed class StubPlanGateResolver : IPlanGateResolver
    {
        private readonly PlanGateDecision _decision;

        public StubPlanGateResolver(PlanGateDecision decision)
        {
            _decision = decision;
        }

        public Task<PlanGateDecision> EvaluateAsync(
            PlanGateContext context, CancellationToken ct = default) =>
            Task.FromResult(_decision);
    }

    internal sealed class InMemoryWhiteboardRepository : IWhiteboardSessionRepository
    {
        public List<WhiteboardSession> AllSessions { get; } = new();
        public List<WhiteboardStepLogEntry> Steps { get; } = new();

        public Task<WhiteboardSession> CreateAsync(
            Guid tenantId, Guid studentSessionId, Guid subjectId, Guid topicId,
            string planTierSnapshot, string sessionMode, CancellationToken ct = default)
        {
            var row = new WhiteboardSession
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                StudentSessionId = studentSessionId,
                SubjectId = subjectId,
                TopicId = topicId,
                PlanTierSnapshot = planTierSnapshot,
                SessionMode = sessionMode,
                StepLog = "[]",
                StartedAt = DateTime.UtcNow,
            };
            AllSessions.Add(row);
            return Task.FromResult(row);
        }

        public Task<WhiteboardSession?> FindAsync(Guid whiteboardSessionId, CancellationToken ct = default)
            => Task.FromResult(AllSessions.Find(s => s.Id == whiteboardSessionId));

        public Task AppendStepAsync(
            WhiteboardSession session, int stepIndex, int drawOpsCount,
            CancellationToken ct = default)
        {
            Steps.Add(new WhiteboardStepLogEntry(stepIndex, drawOpsCount, DateTime.UtcNow));
            return Task.CompletedTask;
        }

        public Task<WhiteboardSession> EndAsync(
            WhiteboardSession session, string endReason, CancellationToken ct = default)
        {
            session.EndedAt = DateTime.UtcNow;
            session.EndReason = endReason;
            return Task.FromResult(session);
        }

        public IReadOnlyList<WhiteboardStepLogEntry> ReadStepLog(WhiteboardSession session)
            => Steps.Where(s => true).ToList();
    }
}
