using System.Collections.Generic;

namespace Muallimi.Api.StudentExperience.Contracts;

/// <summary>
/// T025 — Phase 3 contract catalogue. Enumerates the seven Phase 3
/// contract documents and the endpoints each one owns. Contract tests
/// (under <c>tests/contract/StudentExperience/**</c>) import this class
/// to load the expected endpoint surface and assert the running server
/// matches the published specs in
/// <c>specs/005-student-learning-experience/contracts/</c>.
///
/// Keeping this as code rather than JSON lets the test harness reference
/// it with strong typing; keeping it in a single file lets task T133
/// (update CLAUDE.md + contract catalogue) mirror the list in one place.
/// </summary>
public static class Phase3ContractCatalogue
{
    public record ContractStub(
        string ContractId,
        string Title,
        string OwnerRepository,
        IReadOnlyList<string> ConsumerRepositories,
        IReadOnlyList<EndpointSpec> Endpoints,
        string SpecFile);

    public record EndpointSpec(string Method, string Path, string Purpose);

    public static IReadOnlyList<ContractStub> All { get; } = new List<ContractStub>
    {
        new(
            ContractId: "student.experience.home",
            Title: "Phase 3 Student Experience (home + session lifecycle)",
            OwnerRepository: "muallimi-main-backend",
            ConsumerRepositories: new[] { "Muaallimi-Platform" },
            Endpoints: new EndpointSpec[]
            {
                new("POST", "/student/session/start",        "Create a StudentSession for the authenticated student"),
                new("POST", "/student/session/mode",         "Transition active_mode with plan-gate re-check"),
                new("POST", "/student/session/end",          "Terminate the session and emit session_end"),
                new("GET",  "/student/plan-gate/snapshot",   "Return current plan gate tile state"),
                new("GET",  "/student/home/dashboard",       "Return home dashboard state for the first frame"),
            },
            SpecFile: "specs/005-student-learning-experience/contracts/student-experience-contract.md"),

        new(
            ContractId: "student.lesson.retrieval",
            Title: "Phase 3 Study Mode Lesson Retrieval facade",
            OwnerRepository: "muallimi-main-backend",
            ConsumerRepositories: new[] { "Muaallimi-Platform" },
            Endpoints: new EndpointSpec[]
            {
                new("GET", "/student/study/subjects",                "List enrolled subjects for the active scope"),
                new("GET", "/student/study/subjects/{id}/chapters",  "List chapters under a subject"),
                new("GET", "/student/study/lessons/{id}",            "Return a published lesson with teacher voice binding"),
            },
            SpecFile: "specs/005-student-learning-experience/contracts/lesson-viewer-retrieval-contract.md"),

        new(
            ContractId: "student.tutor.chat",
            Title: "Phase 3 Student Tutor Chat (text + voice)",
            OwnerRepository: "muallimi-main-backend",
            ConsumerRepositories: new[] { "Muaallimi-Platform", "muallimi-ai-service" },
            Endpoints: new EndpointSpec[]
            {
                new("POST", "/student/tutor/text",                     "SSE-streamed text tutor answer"),
                new("POST", "/student/tutor/voice",                    "Voice question submission + AI tutor answer audio"),
                new("GET",  "/student/tutor/voice/playback/{ref}",     "Stream AI tutor voice playback bytes"),
            },
            SpecFile: "specs/005-student-learning-experience/contracts/student-tutor-chat-contract.md"),

        new(
            ContractId: "student.quiz.mock_test",
            Title: "Phase 3 Solve Questions + Mock Test",
            OwnerRepository: "muallimi-main-backend",
            ConsumerRepositories: new[] { "Muaallimi-Platform" },
            Endpoints: new EndpointSpec[]
            {
                new("POST", "/student/solve-questions/start",   "Begin a non-repeating quiz"),
                new("POST", "/student/solve-questions/answer",  "Record an answer + return instant feedback"),
                new("POST", "/student/mock-test/start",         "Begin a timed mock test"),
                new("GET",  "/student/mock-test/state",         "Return authoritative timer + progress"),
                new("POST", "/student/mock-test/submit",        "Submit or time out a mock test"),
            },
            SpecFile: "specs/005-student-learning-experience/contracts/quiz-and-mock-test-contract.md"),

        new(
            ContractId: "student.homework_help",
            Title: "Phase 3 Homework Help (text, voice, image)",
            OwnerRepository: "muallimi-main-backend",
            ConsumerRepositories: new[] { "Muaallimi-Platform", "muallimi-ai-service", "muallimi-document-ingestion" },
            Endpoints: new EndpointSpec[]
            {
                new("POST", "/student/homework-help/submit",  "Submit text/voice/image homework request"),
                new("GET",  "/student/homework-help/{id}",    "Return processed concept + practice response"),
            },
            SpecFile: "specs/005-student-learning-experience/contracts/homework-help-image-contract.md"),

        new(
            ContractId: "student.whiteboard",
            Title: "Phase 3 Live Whiteboard (plan-gated)",
            OwnerRepository: "muallimi-main-backend",
            ConsumerRepositories: new[] { "Muaallimi-Platform" },
            Endpoints: new EndpointSpec[]
            {
                new("POST", "/student/whiteboard/start",  "Begin a whiteboard session with plan + subject gate"),
                new("POST", "/student/whiteboard/step",   "Advance one playback step"),
                new("POST", "/student/whiteboard/end",    "Terminate the whiteboard session"),
            },
            SpecFile: "specs/005-student-learning-experience/contracts/whiteboard-session-contract.md"),

        new(
            ContractId: "student.session_events",
            Title: "Phase 3 Session Events (fan-out to Phase 4)",
            OwnerRepository: "muallimi-main-backend",
            ConsumerRepositories: new[] { "Phase 4 engagement pipeline" },
            Endpoints: new EndpointSpec[]
            {
                new("QUEUE", "student.session.events", "Topic exchange with eleven event_kind routing keys"),
            },
            SpecFile: "specs/005-student-learning-experience/contracts/session-event-contract.md"),
    };
}
