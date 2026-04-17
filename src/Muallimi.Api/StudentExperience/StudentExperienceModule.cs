using Microsoft.AspNetCore.Routing;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.HomeworkHelp;
using Muallimi.Api.StudentExperience.LessonRetrieval;
using Muallimi.Api.StudentExperience.MockTest;
using Muallimi.Api.StudentExperience.PlanGating;
using Muallimi.Api.StudentExperience.QuizDelivery;
using Muallimi.Api.StudentExperience.TutorExposure;
using Muallimi.Api.StudentExperience.Whiteboard;

namespace Muallimi.Api.StudentExperience;

/// <summary>
/// Phase 3 — Student Experience module marker. Individual subfolders
/// (HomeDashboard, LessonRetrieval, TutorExposure, QuizDelivery, MockTest,
/// HomeworkHelp, Whiteboard, StudentSession, PlanGating, SessionEvents,
/// Tenancy, Contracts) own their endpoints, services, and repositories.
/// Endpoint registration is wired per user story.
/// </summary>
public static class StudentExperienceModule
{
    public static IEndpointRouteBuilder MapStudentExperience(this IEndpointRouteBuilder routes)
    {
        // US1 — Home dashboard + session lifecycle
        routes.MapSessionStart();
        routes.MapSessionMode();
        routes.MapSessionEnd();
        routes.MapPlanGateSnapshot();
        // US2 — Study mode lesson retrieval
        routes.MapLessonRetrieval();
        // US3 — Text tutor chat (SSE)
        routes.MapTutorTextChat();
        // US4 — Voice tutor (capture, transcribe, synthesise, stream playback)
        routes.MapTutorVoiceChat();
        routes.MapVoicePlaybackStream();
        // US5 — Solve Questions quiz player
        routes.MapQuizDelivery();
        // US6 — Mock Test timed exam player
        routes.MapMockTest();
        // US7 — Homework Help (text / voice / image)
        routes.MapHomeworkHelp();
        // US8 — Live Whiteboard (plan + subject gated)
        routes.MapWhiteboard();
        return routes;
    }
}
