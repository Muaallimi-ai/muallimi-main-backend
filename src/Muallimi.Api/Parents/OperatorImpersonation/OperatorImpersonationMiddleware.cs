using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Parents.OperatorImpersonation;

/// <summary>
/// T074 (US2) — Operator impersonation detection middleware.
///
/// Runs on every request to a parent-owned route and:
///   1. Refuses requests where the authenticator is neither a parent nor
///      an operator-acting-as-parent (the MVP gate here is header
///      presence; Phase 5 wires this to the Phase 0 identity middleware).
///   2. Tags the <see cref="HttpContext.Items"/> bag with a flag endpoints
///      consult to decide whether to write an
///      <see cref="Muallimi.Domain.Parents.OperatorImpersonationAudit"/>
///      row. The actual audit write happens in the endpoint (see T073)
///      inside the same transaction as the response, per the contract
///      invariant.
///   3. Refuses operator requests that omit the required
///      <c>X-Operator-Reason</c> header — an audit row with a blank reason
///      is a readiness-gate failure.
///
/// <c>OperatorActorId</c> MUST never equal the target parent identifier;
/// the check is duplicated in <see cref="OperatorImpersonationAuditor"/>
/// so the middleware cannot be bypassed.
/// </summary>
public sealed class OperatorImpersonationMiddleware
{
    public const string ItemKey = "OperatorImpersonationContext";

    private readonly RequestDelegate _next;

    public OperatorImpersonationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task Invoke(HttpContext context)
    {
        // Only activate on parent-facing routes.
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/parent/", StringComparison.OrdinalIgnoreCase))
        {
            return _next(context);
        }

        var operatorHeader = context.Request.Headers["X-Operator-Actor-Id"].ToString();
        if (!string.IsNullOrWhiteSpace(operatorHeader))
        {
            if (!Guid.TryParse(operatorHeader, out var operatorId) || operatorId == Guid.Empty)
            {
                return Refuse(context, 400, "invalid_operator_actor");
            }

            var reason = context.Request.Headers["X-Operator-Reason"].ToString();
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Refuse(context, 400, "missing_operator_reason");
            }

            var parentHeader = context.Request.Headers["X-Parent-Profile-Id"].ToString();
            if (Guid.TryParse(parentHeader, out var parentId) && parentId == operatorId)
            {
                return Refuse(context, 400, "operator_equals_parent");
            }

            context.Items[ItemKey] = new OperatorImpersonationContext(operatorId, reason);
        }

        return _next(context);
    }

    private static Task Refuse(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            $"{{\"error\":\"{code}\"}}");
    }
}

public sealed record OperatorImpersonationContext(Guid OperatorActorId, string Reason);

public static class OperatorImpersonationMiddlewareExtensions
{
    public static IApplicationBuilder UseOperatorImpersonation(this IApplicationBuilder app)
        => app.UseMiddleware<OperatorImpersonationMiddleware>();
}
