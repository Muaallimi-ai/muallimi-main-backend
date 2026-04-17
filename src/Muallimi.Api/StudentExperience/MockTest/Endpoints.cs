using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.MockTest;

/// <summary>
/// T096 (US6) — Mock Test endpoints.
///
///   POST /api/student/mock-test/start     — create a MockTestSession with
///                                            server-truth timer and return
///                                            the first question.
///   POST /api/student/mock-test/{id}/answer — record a single answer (or
///                                              flag) while in_progress.
///   GET  /api/student/mock-test/{id}/state — return authoritative timer,
///                                            progress, and current question;
///                                            auto-transitions to timed_out
///                                            when the deadline has passed.
///   POST /api/student/mock-test/{id}/submit — submit (client-initiated or
///                                             post-timeout) and return the
///                                             final score card.
///
/// All routes:
///   - resolve tenant + student session from <c>X-Tenant-Id</c> headers and
///     the request body (same pattern as US1–US5 endpoints);
///   - emit <c>mock_test</c> session events for
///     started / submitted / timed_out / abandoned in the same unit of work
///     that mutates the session — distinct from <c>quiz_answered</c>
///     (enforced by <c>MockTestLabelTests</c>);
///   - propagate the correlation id on every response so cross-repo
///     investigations can trace a run end-to-end.
///
/// Constitution rules: timer truth is server-side (see
/// <c>ClockManipulationTests</c>); questions are sourced from the Phase 1
/// approved question bank via <see cref="MockTestService"/>; the facade
/// never mutates Phase 1 tables.
/// </summary>
public static class MockTestEndpoints
{
    public const string StartRoute = "/api/student/mock-test/start";
    public const string AnswerRoute = "/api/student/mock-test/{id:guid}/answer";
    public const string StateRoute = "/api/student/mock-test/{id:guid}/state";
    public const string SubmitRoute = "/api/student/mock-test/{id:guid}/submit";

    public static IEndpointRouteBuilder MapMockTest(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(StartRoute, HandleStartAsync)
            .WithName("StudentMockTestStart")
            .WithTags("StudentExperience");

        routes.MapPost(AnswerRoute, HandleAnswerAsync)
            .WithName("StudentMockTestAnswer")
            .WithTags("StudentExperience");

        routes.MapGet(StateRoute, HandleStateAsync)
            .WithName("StudentMockTestState")
            .WithTags("StudentExperience");

        routes.MapPost(SubmitRoute, HandleSubmitAsync)
            .WithName("StudentMockTestSubmit")
            .WithTags("StudentExperience");

        return routes;
    }

    private static async Task<IResult> HandleStartAsync(
        HttpContext http,
        MockTestStartRequest request,
        IMockTestService service,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (request is null || request.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "session_id is required." });

        var session = await sessions.FindAsync(request.SessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId) return Results.NotFound();

        var result = await service.StartAsync(session, profile, request, ct);
        switch (result.Outcome)
        {
            case MockTestStartOutcome.InvalidSubject:
                return Results.BadRequest(new { error = "subject_id is not valid for this profile." });
            case MockTestStartOutcome.InvalidTimeLimit:
                return Results.BadRequest(new { error = "time_limit_seconds out of allowed range." });
            case MockTestStartOutcome.InvalidQuestionCount:
                return Results.BadRequest(new { error = "question_count out of allowed range." });
            case MockTestStartOutcome.PlanGated:
                return Results.StatusCode(StatusCodes.Status402PaymentRequired);
            case MockTestStartOutcome.NoQuestionsAvailable:
                return Results.NotFound(new { error = "no_questions_available" });
        }

        await outbox.EnqueueAsync(
            kind: SessionEventKind.mock_test,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                phase = "started",
                mock_test_session_id = result.MockSession!.Id,
                subject_id = request.SubjectId,
                time_limit_seconds = request.TimeLimitSeconds,
                question_bank_snapshot_size = result.Response!.QuestionBankSnapshotSize,
                server_started_at = result.MockSession.ServerStartedAt,
                server_deadline_at = result.MockSession.ServerDeadlineAt,
            },
            curriculumScope: new CurriculumScope(
                CurriculumType: profile.CurriculumType,
                Grade: profile.Grade,
                SubjectId: request.SubjectId,
                ChapterId: null,
                TopicId: null,
                LessonId: null),
            planTierSnapshot: session.PlanTierSnapshot,
            ct: ct);
        await db.SaveChangesAsync(ct);

        PropagateCorrelation(http, session.CorrelationId);
        return Results.Ok(result.Response);
    }

    private static async Task<IResult> HandleAnswerAsync(
        HttpContext http,
        Guid id,
        MockTestAnswerRequest request,
        IMockTestService service,
        IMockTestSessionRepository mockSessions,
        IStudentSessionRepository sessions,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (request is null || string.IsNullOrWhiteSpace(request.QuestionId))
            return Results.BadRequest(new { error = "question_id is required." });

        var mockSession = await mockSessions.FindAsync(id, ct);
        if (mockSession is null || mockSession.TenantId != tenantId) return Results.NotFound();

        var session = await sessions.FindAsync(mockSession.StudentSessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var result = await service.RecordAnswerAsync(session, mockSession, request, ct);
        PropagateCorrelation(http, session.CorrelationId);
        return result.Outcome switch
        {
            MockTestAnswerOutcome.Ok => Results.Ok(new { recorded = true }),
            MockTestAnswerOutcome.AlreadyEnded =>
                Results.Conflict(new { error = "mock_test_already_ended" }),
            MockTestAnswerOutcome.TimedOut =>
                Results.Conflict(new { error = "mock_test_timed_out" }),
            MockTestAnswerOutcome.QuestionNotInSnapshot => Results.NotFound(),
            MockTestAnswerOutcome.InvalidOption =>
                Results.BadRequest(new { error = "chosen_option_id is not part of this question." }),
            _ => Results.Problem(),
        };
    }

    private static async Task<IResult> HandleStateAsync(
        HttpContext http,
        Guid id,
        IMockTestService service,
        IMockTestSessionRepository mockSessions,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        var mockSession = await mockSessions.FindAsync(id, ct);
        if (mockSession is null || mockSession.TenantId != tenantId) return Results.NotFound();

        var session = await sessions.FindAsync(mockSession.StudentSessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var result = await service.GetStateAsync(session, mockSession, ct);

        if (result.Outcome == MockTestStateOutcome.AutoTimedOut)
        {
            await outbox.EnqueueAsync(
                kind: SessionEventKind.mock_test,
                tenantId: session.TenantId,
                studentSessionId: session.Id,
                correlationId: session.CorrelationId,
                payload: new
                {
                    phase = "timed_out",
                    mock_test_session_id = mockSession.Id,
                    server_deadline_at = mockSession.ServerDeadlineAt,
                },
                curriculumScope: new CurriculumScope(
                    CurriculumType: null,
                    Grade: null,
                    SubjectId: mockSession.SubjectId,
                    ChapterId: null,
                    TopicId: null,
                    LessonId: null),
                planTierSnapshot: session.PlanTierSnapshot,
                ct: ct);
            await db.SaveChangesAsync(ct);
        }

        PropagateCorrelation(http, session.CorrelationId);
        return Results.Ok(result.Response);
    }

    private static async Task<IResult> HandleSubmitAsync(
        HttpContext http,
        Guid id,
        IMockTestService service,
        IMockTestSessionRepository mockSessions,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        var mockSession = await mockSessions.FindAsync(id, ct);
        if (mockSession is null || mockSession.TenantId != tenantId) return Results.NotFound();

        var session = await sessions.FindAsync(mockSession.StudentSessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var result = await service.SubmitAsync(session, mockSession, clientInitiated: true, ct);
        if (result.Outcome == MockTestSubmitOutcome.AlreadyEnded)
            return Results.Conflict(new { error = "mock_test_already_ended" });

        await outbox.EnqueueAsync(
            kind: SessionEventKind.mock_test,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                phase = result.TimedOut ? "timed_out" : "submitted",
                mock_test_session_id = mockSession.Id,
                subject_id = mockSession.SubjectId,
                final_score_percent = result.Response!.FinalScore.Percent,
                correct = result.Response.FinalScore.Correct,
                total = result.Response.FinalScore.Total,
            },
            curriculumScope: new CurriculumScope(
                CurriculumType: null,
                Grade: null,
                SubjectId: mockSession.SubjectId,
                ChapterId: null,
                TopicId: null,
                LessonId: null),
            planTierSnapshot: session.PlanTierSnapshot,
            ct: ct);
        await db.SaveChangesAsync(ct);

        PropagateCorrelation(http, session.CorrelationId);
        return Results.Ok(result.Response);
    }

    private static void PropagateCorrelation(HttpContext http, Guid correlationId)
    {
        http.Response.Headers["X-Correlation-Id"] = correlationId.ToString();
    }
}
