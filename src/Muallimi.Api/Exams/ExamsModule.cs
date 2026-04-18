using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Exams.ExamAdministration;
using Muallimi.Api.Exams.ExamCreation;
using Muallimi.Api.Exams.ExamResults;

namespace Muallimi.Api.Exams;

/// <summary>
/// Phase 5 — Exams module marker. Subfolders (ExamCreation,
/// ExamAdministration, ExamResults) own their endpoints, services,
/// repositories, and background services. Wiring is done in US6
/// (T116–T139): creation CRUD + state transitions, student player +
/// submission, and teacher results + publish.
/// </summary>
public static class ExamsModule
{
    public static IEndpointRouteBuilder MapExams(this IEndpointRouteBuilder routes)
    {
        routes.MapExamCreation();
        routes.MapExamPlayer();
        routes.MapExamResults();
        return routes;
    }
}
