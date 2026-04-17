using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.PlanGating;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.HomeDashboard;

/// <summary>
/// T032 (US1) — POST /student/session/mode.
///
/// Transitions the active mode on a <see cref="Muallimi.Domain.StudentExperience.StudentSession"/>
/// and emits the corresponding session event. The plan gate is re-checked
/// on every transition (constitution rule: UI gating is advisory only). A
/// refused transition returns the <see cref="SessionModeRefusedResponse"/>
/// shape with localised reason text; no state change is persisted.
/// </summary>
public static class SessionModeEndpoint
{
    public const string Route = "/api/student/session/mode";

    public static IEndpointRouteBuilder MapSessionMode(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, async (
                HttpContext http,
                SessionModeRequest request,
                IStudentSessionRepository sessions,
                IPlanGateResolver planGate,
                ISessionEventOutboxWriter outbox,
                MuallimiDbContext db,
                CancellationToken ct) =>
            {
                if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
                    return Results.Unauthorized();

                var session = await sessions.FindAsync(request.SessionId, ct);
                if (session is null || session.TenantId != tenantId)
                    return Results.NotFound();

                var decision = await planGate.EvaluateAsync(new PlanGateContext(
                    Mode: request.TargetMode,
                    TenantId: tenantId,
                    PlanTier: session.PlanTierSnapshot,
                    SubjectId: request.TargetScope?.SubjectId,
                    Grade: null), ct);

                if (!decision.Allowed)
                {
                    return Results.Ok(new SessionModeRefusedResponse(
                        SessionId: session.Id,
                        RefusalReason: decision.Reason ?? "plan_gate",
                        RefusalTextAr: LocalisedRefusal(decision.Reason, "ar"),
                        RefusalTextEn: LocalisedRefusal(decision.Reason, "en")));
                }

                var transitioned = await sessions.TransitionModeAsync(session.Id, request.TargetMode, ct);
                if (request.TargetScope is { } scope)
                {
                    transitioned.ActiveSubjectId = scope.SubjectId;
                    transitioned.ActiveChapterId = scope.ChapterId;
                    transitioned.ActiveTopicId = scope.TopicId;
                    transitioned.ActiveLessonId = scope.LessonId;
                    await db.SaveChangesAsync(ct);
                }

                await outbox.EnqueueAsync(
                    kind: MapTargetModeToEventKind(request.TargetMode),
                    tenantId: session.TenantId,
                    studentSessionId: session.Id,
                    correlationId: session.CorrelationId,
                    payload: new
                    {
                        from = session.ActiveMode,
                        to = request.TargetMode,
                        scope = request.TargetScope,
                    },
                    curriculumScope: new CurriculumScope(
                        CurriculumType: session.ActiveCurriculumType,
                        Grade: session.ActiveGrade,
                        SubjectId: request.TargetScope?.SubjectId,
                        ChapterId: request.TargetScope?.ChapterId,
                        TopicId: request.TargetScope?.TopicId,
                        LessonId: request.TargetScope?.LessonId),
                    planTierSnapshot: session.PlanTierSnapshot,
                    ct: ct);
                await db.SaveChangesAsync(ct);

                return Results.Ok(new SessionModeAcceptedResponse(
                    SessionId: transitioned.Id,
                    ActiveMode: transitioned.ActiveMode,
                    ActiveScope: request.TargetScope,
                    TransitionAt: transitioned.SessionLastActivityAt));
            })
            .WithName("StudentSessionMode")
            .WithTags("StudentExperience");
        return routes;
    }

    public static SessionEventKind MapTargetModeToEventKind(string targetMode) =>
        targetMode switch
        {
            StudentModes.Study           => SessionEventKind.lesson_view,
            StudentModes.TutorChat       => SessionEventKind.question_asked,
            StudentModes.TutorVoice      => SessionEventKind.question_asked,
            StudentModes.SolveQuestions  => SessionEventKind.quiz_answered,
            StudentModes.MockTest        => SessionEventKind.mock_test,
            StudentModes.HomeworkHelp    => SessionEventKind.homework_help_used,
            StudentModes.Whiteboard      => SessionEventKind.whiteboard_session,
            _                            => SessionEventKind.session_start,
        };

    public static string LocalisedRefusal(string? reason, string locale)
    {
        return (reason, locale) switch
        {
            ("plan_tier_not_permitted", "ar") => "هذه الميزة متاحة في الخطة المميزة",
            ("plan_tier_not_permitted", "en") => "This feature is available on a higher plan",
            ("subject_not_permitted",   "ar") => "السبورة التفاعلية متاحة لمواد محددة",
            ("subject_not_permitted",   "en") => "Whiteboard is only available for selected subjects",
            ("grade_not_permitted",     "ar") => "هذه الميزة غير متاحة لصفك الدراسي",
            ("grade_not_permitted",     "en") => "This feature is not available for your grade",
            (_, "ar")                         => "لا يمكن الدخول إلى هذه الميزة الآن",
            _                                 => "You can't access this feature right now",
        };
    }
}
