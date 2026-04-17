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

namespace Muallimi.Api.StudentExperience.QuizDelivery;

/// <summary>
/// T085 (US5) — Solve Questions endpoints.
///
///   POST /api/student/solve-questions/start   — create a QuizSession and
///                                                return the first question.
///   POST /api/student/solve-questions/answer  — record an answer + return
///                                                instant feedback + next.
///
/// Both routes:
///   - resolve tenant + student session from <c>X-Tenant-Id</c> headers and
///     the request body (same pattern as US2/US3/US4 endpoints);
///   - enqueue a <c>quiz_answered</c> <see cref="SessionEvent"/> on every
///     answer inside the unit of work that persists the progress row, so
///     the Phase 4 dispatcher sees exactly one event per recorded attempt;
///   - propagate the correlation id on the response so cross-repo
///     investigations can trace the request end-to-end.
///
/// Constitution rules: every question is sourced from the Phase 1 approved
/// question bank projection (<see cref="IQuizQuestionBank"/>); the facade
/// never generates content; non-repetition is enforced server-side via
/// <c>QuizSession.Progress</c>.
/// </summary>
public static class QuizDeliveryEndpoints
{
    public const string StartRoute = "/api/student/solve-questions/start";
    public const string AnswerRoute = "/api/student/solve-questions/answer";

    public static IEndpointRouteBuilder MapQuizDelivery(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(StartRoute, HandleStartAsync)
            .WithName("StudentSolveQuestionsStart")
            .WithTags("StudentExperience");

        routes.MapPost(AnswerRoute, HandleAnswerAsync)
            .WithName("StudentSolveQuestionsAnswer")
            .WithTags("StudentExperience");

        return routes;
    }

    private static async Task<IResult> HandleStartAsync(
        HttpContext http,
        SolveQuestionsStartRequest request,
        IQuizDeliveryService service,
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
            case QuizStartOutcome.InvalidSubject:
                return Results.BadRequest(new { error = "subject_id is not valid for this profile." });
            case QuizStartOutcome.InvalidCount:
                return Results.BadRequest(new { error = "question_count must be between 1 and 20." });
            case QuizStartOutcome.NoQuestionsAvailable:
                return Results.NotFound(new { error = "no_questions_available" });
        }

        PropagateCorrelation(http, session.CorrelationId);
        return Results.Ok(result.Response);
    }

    private static async Task<IResult> HandleAnswerAsync(
        HttpContext http,
        SolveQuestionsAnswerRequest request,
        IQuizDeliveryService service,
        IQuizSessionRepository quizSessions,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (request is null || request.QuizSessionId == Guid.Empty)
            return Results.BadRequest(new { error = "quiz_session_id is required." });

        var quizSession = await quizSessions.FindAsync(request.QuizSessionId, ct);
        if (quizSession is null || quizSession.TenantId != tenantId) return Results.NotFound();

        var session = await sessions.FindAsync(quizSession.StudentSessionId, ct);
        if (session is null || session.TenantId != tenantId) return Results.NotFound();

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId) return Results.NotFound();

        var result = await service.AnswerAsync(session, quizSession, request, ct);
        switch (result.Outcome)
        {
            case QuizAnswerOutcome.QuizNotFound:
            case QuizAnswerOutcome.QuestionNotInSnapshot:
                return Results.NotFound();
            case QuizAnswerOutcome.QuizAlreadyEnded:
                return Results.Conflict(new { error = "quiz_already_ended" });
            case QuizAnswerOutcome.QuestionAlreadyAnswered:
                return Results.Conflict(new { error = "question_already_answered" });
            case QuizAnswerOutcome.InvalidOption:
                return Results.BadRequest(new { error = "chosen_option_id is not part of this question." });
        }

        await outbox.EnqueueAsync(
            kind: SessionEventKind.quiz_answered,
            tenantId: session.TenantId,
            studentSessionId: session.Id,
            correlationId: session.CorrelationId,
            payload: new
            {
                quiz_session_id = quizSession.Id,
                question_id = result.AnsweredRecord?.QuestionId,
                chosen_option_id = result.ChosenOptionId,
                is_correct = result.Response?.IsCorrect,
                quiz_complete = result.QuizComplete,
            },
            curriculumScope: new CurriculumScope(
                CurriculumType: profile.CurriculumType,
                Grade: profile.Grade,
                SubjectId: quizSession.SubjectId,
                ChapterId: quizSession.ChapterId,
                TopicId: quizSession.TopicId,
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
