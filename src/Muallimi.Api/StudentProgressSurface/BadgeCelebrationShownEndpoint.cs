using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.StudentProgressSurface;

/// <summary>
/// T054 (US1) — POST /student/progress/badges/{badge_award_id}/celebration-shown.
///
/// Flips the <c>celebration_shown</c> bit so the non-blocking celebration
/// affordance does not replay on the next progress-surface render. The flag
/// is monotonic (Phase 4 data-model constraint); a second call returns a
/// <c>200</c> with <c>already_shown</c> so the client treats replay as a
/// no-op.
/// </summary>
public static class BadgeCelebrationShownEndpoint
{
    public const string Route = "/api/student/progress/badges/{badgeAwardId:guid}/celebration-shown";

    public static IEndpointRouteBuilder MapBadgeCelebrationShown(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleAsync)
            .WithName("StudentBadgeCelebrationShown")
            .WithTags("StudentProgressSurface");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        Guid badgeAwardId,
        IStudentProgressService service,
        CancellationToken ct)
    {
        if (!StudentProgressHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!StudentProgressHeaders.TryGetStudentProfileId(http, out var studentId))
            return Results.Unauthorized();

        var correlationId = StudentProgressHeaders.ResolveCorrelationId(http);
        var outcome = await service.MarkBadgeCelebrationShownAsync(tenantId, studentId, badgeAwardId, ct);
        http.Response.Headers["X-Correlation-Id"] = correlationId.ToString();

        return outcome switch
        {
            BadgeCelebrationOutcome.Marked => Results.Ok(new { status = "marked", correlation_id = correlationId }),
            BadgeCelebrationOutcome.AlreadyShown => Results.Ok(new { status = "already_shown", correlation_id = correlationId }),
            BadgeCelebrationOutcome.NotFound => Results.NotFound(new { error = "badge_award_not_found" }),
            _ => Results.Problem("Unexpected badge celebration outcome."),
        };
    }
}
