using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.StudentExperience.SessionEvents;
using Muallimi.Api.StudentExperience.StudentSession;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.HomeDashboard;

/// <summary>
/// T031 (US1) — POST /student/session/start.
///
/// Creates a new <see cref="StudentSession"/> for the authenticated student
/// on home-dashboard load, builds the initial <see cref="HomeDashboardState"/>,
/// and writes a <c>session_start</c> row to the SessionEvent outbox in the
/// same unit of work.
///
/// Contract requires the response to echo the inbound correlation_id and
/// include the full mode_tile_states array. Tenant is resolved from the
/// <c>X-Tenant-Id</c> header (see <c>HttpTenantContextAccessor</c>) so the
/// DbContext's global query filter scopes the profile lookup.
/// </summary>
public static class SessionStartEndpoint
{
    public const string Route = "/api/student/session/start";

    public static IEndpointRouteBuilder MapSessionStart(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, async (
                HttpContext http,
                SessionStartRequest request,
                IStudentSessionRepository sessions,
                IHomeDashboardService dashboard,
                ISessionEventOutboxWriter outbox,
                MuallimiDbContext db,
                CancellationToken ct) =>
            {
                if (!TryGetTenantId(http, out var tenantId))
                    return Results.Unauthorized();

                var profile = await db.StudentProfiles
                    .FirstOrDefaultAsync(p => p.Id == request.StudentProfileId, ct);
                if (profile is null || profile.TenantId != tenantId)
                    return Results.Forbid();

                var correlationId = ResolveCorrelationId(http);
                var tutorLanguage = ResolveTutorLanguage(request.PreferredLanguage, profile);

                var session = await sessions.StartAsync(
                    tenantId: tenantId,
                    studentProfileId: profile.Id,
                    correlationId: correlationId,
                    tutorLanguage: tutorLanguage,
                    deviceClass: NormaliseDeviceClass(request.DeviceClass),
                    planTierSnapshot: profile.PlanTier,
                    ct: ct);

                var state = await dashboard.BuildAsync(session, profile, ct);

                await outbox.EnqueueAsync(
                    kind: SessionEventKind.session_start,
                    tenantId: session.TenantId,
                    studentSessionId: session.Id,
                    correlationId: session.CorrelationId,
                    payload: new
                    {
                        tutor_language = session.TutorLanguage,
                        device_class = session.DeviceClass,
                        plan_tier = session.PlanTierSnapshot,
                    },
                    curriculumScope: new CurriculumScope(
                        CurriculumType: profile.CurriculumType,
                        Grade: profile.Grade,
                        SubjectId: null,
                        ChapterId: null,
                        TopicId: null,
                        LessonId: null),
                    planTierSnapshot: session.PlanTierSnapshot,
                    ct: ct);

                await db.SaveChangesAsync(ct);

                http.Response.Headers["X-Correlation-Id"] = session.CorrelationId.ToString();
                return Results.Ok(new SessionStartResponse(
                    SessionId: state.SessionId,
                    CorrelationId: state.CorrelationId,
                    TutorLanguage: state.TutorLanguage,
                    CurriculumType: state.CurriculumType,
                    Grade: state.Grade,
                    PlanTierSnapshot: state.PlanTierSnapshot,
                    ModeTileStates: state.ModeTileStates,
                    ResumeTarget: state.ResumeTarget,
                    RecommendedTopics: state.RecommendedTopics,
                    GreetingTextAr: state.GreetingTextAr,
                    GreetingTextEn: state.GreetingTextEn));
            })
            .WithName("StudentSessionStart")
            .WithTags("StudentExperience");
        return routes;
    }

    public static bool TryGetTenantId(HttpContext http, out Guid tenantId)
    {
        var raw = http.Request.Headers["X-Tenant-Id"].ToString();
        return Guid.TryParse(raw, out tenantId);
    }

    public static Guid ResolveCorrelationId(HttpContext http)
    {
        var fromItems = http.Items["CorrelationId"]?.ToString();
        var fromHeader = http.Request.Headers["X-Correlation-Id"].ToString();
        if (Guid.TryParse(fromItems, out var a)) return a;
        if (Guid.TryParse(fromHeader, out var b)) return b;
        return Guid.NewGuid();
    }

    public static string ResolveTutorLanguage(string preferred, StudentProfile profile)
    {
        var normalised = preferred?.Trim().ToLowerInvariant();
        if (normalised is "ar" or "en") return normalised;
        return profile.PreferredLanguage;
    }

    public static string NormaliseDeviceClass(string deviceClass) =>
        deviceClass switch
        {
            "mobile_small" or "tablet" or "desktop" => deviceClass,
            _ => "desktop",
        };
}
