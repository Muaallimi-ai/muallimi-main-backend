using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Engagement.InterventionPrompts;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.AtRiskDetection;

/// <summary>
/// T149 (US8) — GET /api/parent/at-risk/{child_id}.
///
/// Returns the active at-risk flag and the linked intervention prompt for
/// the child. Tenant isolation: the calling parent's tenant is matched
/// against the stored <c>tenant_id</c>; cross-family access returns 404
/// (not 403) to avoid leaking child existence.
///
/// Operator impersonation is audited on every render against the
/// <c>intervention_prompt</c> surface.
/// </summary>
public static class ParentAtRiskEndpoint
{
    public const string Route = "/api/parent/at-risk/{childId:guid}";

    public static IEndpointRouteBuilder MapParentAtRisk(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("ParentAtRisk")
            .WithTags("AtRisk");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        Guid childId,
        HttpContext http,
        IAtRiskFlagRepository flags,
        IInterventionPromptRepository prompts,
        IChildLinkRepository links,
        IOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var isImpersonation = ParentDashboardHeaders.TryGetOperatorContext(
            http, out var operatorActorId, out var reason);

        var link = await links.GetActiveAsync(tenantId, parentProfileId, childId, ct);
        if (link is null) return Results.NotFound();

        if (isImpersonation)
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                targetParentProfileId: parentProfileId,
                targetChildId: childId,
                surface: OperatorImpersonationSurfaces.InterventionPrompt,
                reason: string.IsNullOrWhiteSpace(reason) ? "at_risk_view" : reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;

        var flag = await flags.GetActiveAsync(tenantId, childId, ct);
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

internal static class AtRiskResponseProjection
{
    public static object Build(AtRiskFlag flag, InterventionPrompt? prompt, string correlationId)
    {
        object? promptPayload = prompt is null
            ? null
            : new
            {
                intervention_prompt_id = prompt.InterventionPromptId,
                body_ar = prompt.BodyAr,
                body_en = prompt.BodyEn,
                next_step = ParseNextStep(prompt.NextStep),
                guardrail_decision_trail_id = prompt.GuardrailDecisionTrailId,
                created_at = prompt.CreatedAt,
            };

        return new
        {
            active = true,
            at_risk_flag_id = flag.AtRiskFlagId,
            raised_at = flag.RaisedAt,
            threshold_version = flag.ThresholdVersion,
            triggering_evidence = ParseEvidence(flag.TriggeringEvidence),
            intervention_prompt = promptPayload,
            correlation_id = correlationId,
        };
    }

    private static JsonElement ParseEvidence(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}").RootElement.Clone();
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static object ParseNextStep(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new { phase3_mode = "review", deep_link = "/" };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string mode = root.TryGetProperty("phase3_mode", out var m) ? m.GetString() ?? "review" : "review";
            string link = root.TryGetProperty("deep_link", out var d) ? d.GetString() ?? "/" : "/";
            return new { phase3_mode = mode, deep_link = link };
        }
        catch
        {
            return new { phase3_mode = "review", deep_link = "/" };
        }
    }
}
