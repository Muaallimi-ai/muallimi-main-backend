using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.StudentProgressSurface;

/// <summary>
/// T053 (US1) — GET /student/progress/focus-area/{focus_area_id}.
///
/// Returns the full focus-area record (summary + signal summary + next-step
/// deep link + correlation id) so the student progress surface can render
/// the "why this focus area" detail panel.
///
/// Tenant + student isolation applies; a focus-area owned by a different
/// student returns <c>404</c>, not <c>403</c>, so existence cannot be probed
/// across tenants.
/// </summary>
public static class FocusAreaDetailEndpoint
{
    public const string Route = "/api/student/progress/focus-area/{focusAreaId:guid}";

    public static IEndpointRouteBuilder MapFocusAreaDetail(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("StudentFocusAreaDetail")
            .WithTags("StudentProgressSurface");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        Guid focusAreaId,
        IStudentProgressService service,
        CancellationToken ct)
    {
        if (!StudentProgressHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!StudentProgressHeaders.TryGetStudentProfileId(http, out var studentId))
            return Results.Unauthorized();

        var correlationId = StudentProgressHeaders.ResolveCorrelationId(http);
        var detail = await service.GetFocusAreaDetailAsync(tenantId, studentId, focusAreaId, ct);
        http.Response.Headers["X-Correlation-Id"] = correlationId.ToString();

        if (detail is null) return Results.NotFound(new { error = "focus_area_not_found" });

        return Results.Ok(new
        {
            focus_area_id = detail.Summary.FocusAreaId,
            subject_id = detail.Summary.SubjectId,
            chapter_id = detail.Summary.ChapterId,
            topic_id = detail.Summary.TopicId,
            rationale_ar = detail.Summary.RationaleAr,
            rationale_en = detail.Summary.RationaleEn,
            suggested_next_step = new
            {
                phase3_mode = detail.Summary.SuggestedNextStep.Phase3Mode,
                deep_link = detail.Summary.SuggestedNextStep.DeepLink,
            },
            signal_summary = detail.SignalSummary,
            computed_at = detail.ComputedAt,
            valid_until = detail.ValidUntil,
            correlation_id = correlationId,
        });
    }
}
