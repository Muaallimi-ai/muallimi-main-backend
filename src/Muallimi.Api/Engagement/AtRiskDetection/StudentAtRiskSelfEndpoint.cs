using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Engagement.InterventionPrompts;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T150 (US8) — GET /api/student/at-risk/self.
///
/// Returns the student-facing intervention prompt when the authenticated
/// student has an active flag. The student-facing body is identical to the
/// parent-facing body and is enforced to be neutral and supportive at
/// generation time (see <see cref="InterventionPromptGenerator"/>).
/// </summary>
public static class StudentAtRiskSelfEndpoint
{
    public const string Route = "/api/student/at-risk/self";
    public const string TenantHeaderName = "X-Tenant-Id";
    public const string StudentProfileHeaderName = "X-Student-Profile-Id";
    public const string CorrelationHeaderName = "X-Correlation-Id";

    public static IEndpointRouteBuilder MapStudentAtRiskSelf(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("StudentAtRiskSelf")
            .WithTags("AtRisk");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        IAtRiskFlagRepository flags,
        IInterventionPromptRepository prompts,
        CancellationToken ct)
    {
        if (!Guid.TryParse(http.Request.Headers[TenantHeaderName].ToString(), out var tenantId))
            return Results.Unauthorized();
        if (!Guid.TryParse(http.Request.Headers[StudentProfileHeaderName].ToString(), out var studentId))
            return Results.Unauthorized();

        var rawCorr = http.Request.Headers[CorrelationHeaderName].ToString();
        var correlationId = string.IsNullOrWhiteSpace(rawCorr) ? Guid.NewGuid().ToString("D") : rawCorr;
        http.Response.Headers["X-Correlation-Id"] = correlationId;

        var flag = await flags.GetActiveAsync(tenantId, studentId, ct);
        if (flag is null)
        {
            return Results.Ok(new { active = false });
        }

        InterventionPrompt? prompt = null;
        if (flag.LinkedInterventionPromptId.HasValue)
        {
            prompt = await prompts.GetByIdAsync(tenantId, flag.LinkedInterventionPromptId.Value, ct);
        }

        return Results.Ok(AtRiskResponseProjection.Build(flag, prompt, correlationId));
    }
}
