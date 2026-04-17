using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.LessonRetrieval;

/// <summary>
/// T048 (US2) — Study mode lesson retrieval endpoints. The three routes
/// map the Phase 3 lesson viewer contract:
///
///   GET /api/student/study/subjects
///   GET /api/student/study/subjects/{subject_id}/chapters
///   GET /api/student/study/lessons/{lesson_id}
///
/// All three require <c>X-Tenant-Id</c> and <c>X-Session-Id</c>. Lesson
/// fetch emits a <c>lesson_view</c> SessionEvent via the transactional
/// outbox in the same unit of work so Phase 4 consumers see the event
/// exactly once. Approval-state and tenant filters are re-applied inside
/// <see cref="ILessonRetrievalService"/>; this endpoint layer only owns
/// correlation propagation and event emission.
/// </summary>
public static class LessonRetrievalEndpoints
{
    public static IEndpointRouteBuilder MapLessonRetrieval(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/student/study/subjects", HandleSubjectsAsync)
            .WithName("StudentStudySubjects")
            .WithTags("StudentExperience");

        routes.MapGet("/api/student/study/subjects/{subjectId:guid}/chapters", HandleChaptersAsync)
            .WithName("StudentStudyChapters")
            .WithTags("StudentExperience");

        routes.MapGet("/api/student/study/lessons/{lessonId:guid}", HandleLessonAsync)
            .WithName("StudentStudyLesson")
            .WithTags("StudentExperience");

        return routes;
    }

    private static async Task<IResult> HandleSubjectsAsync(
        HttpContext http,
        ILessonRetrievalService retrieval,
        IStudentSessionRepository sessions,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, sessions, db, ct, out var scopeTask))
            return Results.Unauthorized();
        var scope = await scopeTask!;
        if (scope is null) return Results.NotFound();

        var response = await retrieval.ListSubjectsAsync(scope.Session, scope.Profile, ct);
        PropagateCorrelation(http, scope.Session.CorrelationId);
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleChaptersAsync(
        Guid subjectId,
        HttpContext http,
        ILessonRetrievalService retrieval,
        IStudentSessionRepository sessions,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, sessions, db, ct, out var scopeTask))
            return Results.Unauthorized();
        var scope = await scopeTask!;
        if (scope is null) return Results.NotFound();

        var response = await retrieval.ListChaptersAsync(scope.Session, scope.Profile, subjectId, ct);
        if (response is null) return Results.NotFound();
        PropagateCorrelation(http, scope.Session.CorrelationId);
        return Results.Ok(response);
    }

    private static async Task<IResult> HandleLessonAsync(
        Guid lessonId,
        HttpContext http,
        ILessonRetrievalService retrieval,
        IStudentSessionRepository sessions,
        ISessionEventOutboxWriter outbox,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        if (!TryResolveScope(http, sessions, db, ct, out var scopeTask))
            return Results.Unauthorized();
        var scope = await scopeTask!;
        if (scope is null) return Results.NotFound();

        var response = await retrieval.GetLessonAsync(scope.Session, scope.Profile, lessonId, ct);
        if (response is null) return Results.NotFound();

        await outbox.EnqueueAsync(
            kind: SessionEventKind.lesson_view,
            tenantId: scope.Session.TenantId,
            studentSessionId: scope.Session.Id,
            correlationId: scope.Session.CorrelationId,
            payload: new
            {
                lesson_id = response.LessonId,
                subject_id = response.SubjectId,
                chapter_id = response.ChapterId,
                topic_id = response.TopicId,
                teacher_voice_profile_id = response.TeacherVoiceProfileId,
                teacher_voice_profile_source = response.TeacherVoiceProfileSource,
            },
            curriculumScope: new CurriculumScope(
                CurriculumType: scope.Profile.CurriculumType,
                Grade: scope.Profile.Grade,
                SubjectId: response.SubjectId,
                ChapterId: response.ChapterId,
                TopicId: response.TopicId,
                LessonId: response.LessonId),
            planTierSnapshot: scope.Session.PlanTierSnapshot,
            ct: ct);
        await db.SaveChangesAsync(ct);

        PropagateCorrelation(http, scope.Session.CorrelationId);
        return Results.Ok(response);
    }

    private static bool TryResolveScope(
        HttpContext http,
        IStudentSessionRepository sessions,
        MuallimiDbContext db,
        CancellationToken ct,
        out Task<StudentSessionScope?>? scopeTask)
    {
        scopeTask = null;
        if (!SessionStartEndpoint.TryGetTenantId(http, out var tenantId))
            return false;

        var sessionIdRaw = http.Request.Headers["X-Session-Id"].ToString();
        if (!Guid.TryParse(sessionIdRaw, out var sessionId))
            return false;

        scopeTask = LoadScopeAsync(tenantId, sessionId, sessions, db, ct);
        return true;
    }

    private static async Task<StudentSessionScope?> LoadScopeAsync(
        Guid tenantId,
        Guid sessionId,
        IStudentSessionRepository sessions,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(sessionId, ct);
        if (session is null || session.TenantId != tenantId) return null;

        var profile = await db.StudentProfiles
            .FirstOrDefaultAsync(p => p.Id == session.StudentProfileId, ct);
        if (profile is null || profile.TenantId != tenantId) return null;

        return new StudentSessionScope(session, profile);
    }

    private static void PropagateCorrelation(HttpContext http, Guid correlationId)
    {
        http.Response.Headers["X-Correlation-Id"] = correlationId.ToString();
    }

    private sealed record StudentSessionScope(
        Muallimi.Domain.StudentExperience.StudentSession Session,
        Muallimi.Domain.StudentExperience.StudentProfile Profile);
}
