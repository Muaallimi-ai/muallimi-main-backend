using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.HomeDashboard;

/// <summary>
/// T033 (US1) — POST /student/session/end.
///
/// Terminates the active session and emits a <c>session_end</c> outbox
/// event with the <c>end_reason</c>. Idempotent: a second call on an
/// already-ended session returns the existing <c>ended_at</c> without
/// emitting a duplicate event.
/// </summary>
public static class SessionEndEndpoint
{
    public const string Route = "/api/student/session/end";

    private static readonly string[] AllowedReasons =
        { "signed_out", "timeout", "tab_closed", "switched_profile" };

    public static IEndpointRouteBuilder MapSessionEnd(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, async (
                HttpContext http,
                SessionEndRequest request,
                IStudentSessionRepository sessions,
                ISessionEventOutboxWriter outbox,
                MuallimiDbContext db,
                CancellationToken ct) =>
            {
                if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
                    return Results.Unauthorized();

                var session = await sessions.FindAsync(request.SessionId, ct);
                if (session is null || session.TenantId != tenantId)
                    return Results.NotFound();

                if (session.SessionEndedAt is DateTime alreadyEnded)
                {
                    return Results.Ok(new SessionEndResponse(session.Id, alreadyEnded));
                }

                var reason = Array.IndexOf(AllowedReasons, request.EndReason) >= 0
                    ? request.EndReason
                    : "tab_closed";

                var ended = await sessions.EndAsync(session.Id, reason, ct);

                await outbox.EnqueueAsync(
                    kind: SessionEventKind.session_end,
                    tenantId: ended.TenantId,
                    studentSessionId: ended.Id,
                    correlationId: ended.CorrelationId,
                    payload: new { end_reason = reason },
                    curriculumScope: new CurriculumScope(
                        CurriculumType: ended.ActiveCurriculumType,
                        Grade: ended.ActiveGrade,
                        SubjectId: ended.ActiveSubjectId,
                        ChapterId: ended.ActiveChapterId,
                        TopicId: ended.ActiveTopicId,
                        LessonId: ended.ActiveLessonId),
                    planTierSnapshot: ended.PlanTierSnapshot,
                    ct: ct);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new SessionEndResponse(ended.Id, ended.SessionEndedAt!.Value));
            })
            .WithName("StudentSessionEnd")
            .WithTags("StudentExperience");
        return routes;
    }
}
