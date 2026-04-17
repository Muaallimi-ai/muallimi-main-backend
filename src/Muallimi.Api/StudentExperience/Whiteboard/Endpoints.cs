using System;
using System.Text.Json;
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

namespace Muallimi.Api.StudentExperience.Whiteboard;

/// <summary>
/// T118 (US8) — Live Whiteboard endpoints.
///
///   POST /api/student/whiteboard/start       — create a WhiteboardSession
///                                               with plan + subject gate
///                                               re-check; refusals still
///                                               return 200 with a refusal
///                                               envelope so the client can
///                                               surface the localized text.
///   POST /api/student/whiteboard/{id}/step   — advance one step; returns
///                                               primitive draw ops + narration
///                                               grounded in an approved
///                                               Phase 1 chunk. If the plan
///                                               is revoked mid-run the
///                                               session is ended with
///                                               <c>gate_revoked</c> here.
///   POST /api/student/whiteboard/{id}/end    — terminate the session and
///                                               emit a <c>whiteboard_session</c>
///                                               event with <c>steps_played</c>.
///
/// A <c>whiteboard_session</c> event is emitted in the same unit of work as
/// the terminating row write so the Phase 4 fan-out is atomic with the
/// mutation. A gate-revoked auto-end also emits the event so the engagement
/// pipeline sees every session close.
///
/// Every response propagates <c>X-Correlation-Id</c> for cross-repo
/// investigations.
/// </summary>
public static class WhiteboardEndpoints
{
    public const string StartRoute = "/api/student/whiteboard/start";
    public const string StepRoute = "/api/student/whiteboard/{id:guid}/step";
    public const string EndRoute = "/api/student/whiteboard/{id:guid}/end";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapWhiteboard(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(StartRoute, HandleStartAsync)
            .WithName("StudentWhiteboardStart")
            .WithTags("StudentExperience");

        routes.MapPost(StepRoute, HandleStepAsync)
            .WithName("StudentWhiteboardStep")
            .WithTags("StudentExperience");

        routes.MapPost(EndRoute, HandleEndAsync)
            .WithName("StudentWhiteboardEnd")
            .WithTags("StudentExperience");

        return routes;
    }

    public static async Task<IResult> HandleStartAsync(
        HttpContext http,
        WhiteboardStartRequest request,
        IWhiteboardService service,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (request is null)
            return Results.BadRequest(new { error = "request body is required." });
        if (request.SessionId == Guid.Empty)
            return Results.BadRequest(new { error = "session_id is required." });

        var session = await sessions.FindAsync(request.SessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId) return Results.NotFound();

        var result = await service.StartAsync(session, profile, request, ct);
        PropagateCorrelation(http, session.CorrelationId);

        switch (result.Outcome)
        {
            case WhiteboardStartOutcome.InvalidRequest:
                return Results.BadRequest(new { error = result.Error });
            case WhiteboardStartOutcome.Refused:
                // Refusals are persisted implicitly on the upstream refusal
                // shape plus a plan_gate session event so analytics can see
                // gated attempts. We do not create a WhiteboardSession row
                // for a refused start (there is no session to audit).
                await outbox.EnqueueAsync(
                    kind: SessionEventKind.whiteboard_session,
                    tenantId: session.TenantId,
                    studentSessionId: session.Id,
                    correlationId: session.CorrelationId,
                    payload: new
                    {
                        phase = "refused",
                        subject_id = request.SubjectId,
                        topic_id = request.TopicId,
                        refusal_reason = result.Response!.RefusalReason,
                        steps_played = 0,
                    },
                    curriculumScope: new CurriculumScope(
                        CurriculumType: profile.CurriculumType,
                        Grade: profile.Grade,
                        SubjectId: request.SubjectId,
                        ChapterId: null,
                        TopicId: request.TopicId,
                        LessonId: null),
                    planTierSnapshot: session.PlanTierSnapshot,
                    ct: ct);
                await db.SaveChangesAsync(ct);
                return Results.Json(result.Response, SerializerOptions);
            case WhiteboardStartOutcome.Accepted:
                await outbox.EnqueueAsync(
                    kind: SessionEventKind.whiteboard_session,
                    tenantId: session.TenantId,
                    studentSessionId: session.Id,
                    correlationId: session.CorrelationId,
                    payload: new
                    {
                        phase = "started",
                        whiteboard_session_id = result.WhiteboardSession!.Id,
                        subject_id = result.WhiteboardSession.SubjectId,
                        topic_id = result.WhiteboardSession.TopicId,
                        session_mode = result.WhiteboardSession.SessionMode,
                        started_at = result.WhiteboardSession.StartedAt,
                        steps_played = 0,
                    },
                    curriculumScope: new CurriculumScope(
                        CurriculumType: profile.CurriculumType,
                        Grade: profile.Grade,
                        SubjectId: result.WhiteboardSession.SubjectId,
                        ChapterId: null,
                        TopicId: result.WhiteboardSession.TopicId,
                        LessonId: null),
                    planTierSnapshot: session.PlanTierSnapshot,
                    ct: ct);
                await db.SaveChangesAsync(ct);
                return Results.Json(result.Response, SerializerOptions);
            default:
                return Results.Problem();
        }
    }

    public static async Task<IResult> HandleStepAsync(
        HttpContext http,
        Guid id,
        WhiteboardStepRequest request,
        IWhiteboardService service,
        IWhiteboardSessionRepository whiteboards,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (request is null)
            return Results.BadRequest(new { error = "request body is required." });

        var whiteboard = await whiteboards.FindAsync(id, ct);
        if (whiteboard is null || whiteboard.TenantId != tenantId) return Results.NotFound();

        var session = await sessions.FindAsync(whiteboard.StudentSessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId) return Results.NotFound();

        var effectiveRequest = request with { WhiteboardSessionId = whiteboard.Id };
        var result = await service.StepAsync(session, profile, whiteboard, effectiveRequest, ct);
        PropagateCorrelation(http, session.CorrelationId);

        switch (result.Outcome)
        {
            case WhiteboardStepOutcome.Ok:
                return Results.Json(result.Response, SerializerOptions);
            case WhiteboardStepOutcome.AlreadyEnded:
                return Results.Conflict(new { error = "whiteboard_already_ended" });
            case WhiteboardStepOutcome.InvalidStepIndex:
                return Results.BadRequest(new { error = "requested_step_index out of range." });
            case WhiteboardStepOutcome.NoContent:
                return Results.NotFound(new { error = "no_approved_content" });
            case WhiteboardStepOutcome.GateRevoked:
                var steps = whiteboards.ReadStepLog(whiteboard).Count;
                await whiteboards.EndAsync(whiteboard, WhiteboardEndReasons.GateRevoked, ct);
                await outbox.EnqueueAsync(
                    kind: SessionEventKind.whiteboard_session,
                    tenantId: session.TenantId,
                    studentSessionId: session.Id,
                    correlationId: session.CorrelationId,
                    payload: new
                    {
                        phase = "gate_revoked",
                        whiteboard_session_id = whiteboard.Id,
                        subject_id = whiteboard.SubjectId,
                        topic_id = whiteboard.TopicId,
                        revoked_reason = result.RevokedReason,
                        steps_played = steps,
                        end_reason = WhiteboardEndReasons.GateRevoked,
                    },
                    curriculumScope: new CurriculumScope(
                        CurriculumType: profile.CurriculumType,
                        Grade: profile.Grade,
                        SubjectId: whiteboard.SubjectId,
                        ChapterId: null,
                        TopicId: whiteboard.TopicId,
                        LessonId: null),
                    planTierSnapshot: session.PlanTierSnapshot,
                    ct: ct);
                await db.SaveChangesAsync(ct);
                return Results.StatusCode(StatusCodes.Status402PaymentRequired);
            default:
                return Results.Problem();
        }
    }

    public static async Task<IResult> HandleEndAsync(
        HttpContext http,
        Guid id,
        WhiteboardEndRequest request,
        IWhiteboardService service,
        IWhiteboardSessionRepository whiteboards,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (request is null)
            return Results.BadRequest(new { error = "request body is required." });

        var whiteboard = await whiteboards.FindAsync(id, ct);
        if (whiteboard is null || whiteboard.TenantId != tenantId) return Results.NotFound();

        var session = await sessions.FindAsync(whiteboard.StudentSessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var effectiveRequest = request with { WhiteboardSessionId = whiteboard.Id };
        var result = await service.EndAsync(session, whiteboard, effectiveRequest, ct);
        PropagateCorrelation(http, session.CorrelationId);

        switch (result.Outcome)
        {
            case WhiteboardEndOutcome.InvalidReason:
                return Results.BadRequest(new { error = "end_reason is invalid." });
            case WhiteboardEndOutcome.AlreadyEnded:
                return Results.Conflict(new { error = "whiteboard_already_ended" });
            case WhiteboardEndOutcome.Ok:
                await outbox.EnqueueAsync(
                    kind: SessionEventKind.whiteboard_session,
                    tenantId: session.TenantId,
                    studentSessionId: session.Id,
                    correlationId: session.CorrelationId,
                    payload: new
                    {
                        phase = "ended",
                        whiteboard_session_id = whiteboard.Id,
                        subject_id = whiteboard.SubjectId,
                        topic_id = whiteboard.TopicId,
                        end_reason = result.Response!.EndReason,
                        steps_played = result.StepsPlayed,
                        ended_at = result.Response.EndedAt,
                    },
                    curriculumScope: new CurriculumScope(
                        CurriculumType: null,
                        Grade: null,
                        SubjectId: whiteboard.SubjectId,
                        ChapterId: null,
                        TopicId: whiteboard.TopicId,
                        LessonId: null),
                    planTierSnapshot: session.PlanTierSnapshot,
                    ct: ct);
                await db.SaveChangesAsync(ct);
                return Results.Json(result.Response, SerializerOptions);
            default:
                return Results.Problem();
        }
    }

    private static void PropagateCorrelation(HttpContext http, Guid correlationId)
    {
        http.Response.Headers["X-Correlation-Id"] = correlationId.ToString();
    }
}
