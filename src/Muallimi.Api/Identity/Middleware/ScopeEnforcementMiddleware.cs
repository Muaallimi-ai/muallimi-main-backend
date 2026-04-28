using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Identity.Middleware;

/// <summary>
/// Add-child redesign Phase 7 — scope enforcement. JWTs minted for
/// Managed users carry <c>scope=child</c>; JWTs for Personal users
/// carry <c>scope=parent</c>. This middleware rejects child-scope
/// requests against parent-only path prefixes with 403 + the standard
/// error code <c>child_scope_blocked</c>.
///
/// Deny-list source (state-of-truth = add-child-implementation-state.md
/// section 7.2):
/// • Phase 6 billing/subscription/payment/invoice
/// • Phase 4 parent dashboard / children / notifications / at-risk /
///   weekly-report
/// • Identity admin (/api/auth/admin)
/// • Identity parent-children (/api/auth/parent/children) — except
///   the exit-child-session route which a child JWT must hit.
/// • Operator endpoints
///
/// Public endpoints (login, lookup-method, refresh, etc.) sit at
/// /api/auth/* but most do not carry an authenticated scope, so the
/// `scope == "child"` guard is the only check we apply.
/// </summary>
public sealed class ScopeEnforcementMiddleware
{
    private static readonly string[] DenyForChild =
    {
        "/api/billing",
        "/api/subscriptions",
        "/api/payments",
        "/api/invoices",
        "/api/parent",
        "/api/auth/parent/children",
        "/api/auth/admin",
        "/api/operator",
        "/api/saas-operations",
    };

    /// <summary>Routes a child JWT IS allowed to hit even though the prefix above would block them.</summary>
    private static readonly string[] AllowForChild =
    {
        "/api/auth/parent/exit-child-session",
    };

    private readonly RequestDelegate _next;

    public ScopeEnforcementMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var scope = context.User.FindFirst("scope")?.Value;
            if (string.Equals(scope, "child", StringComparison.Ordinal))
            {
                var path = context.Request.Path.Value ?? string.Empty;

                var allowed = false;
                foreach (var allow in AllowForChild)
                {
                    if (path.StartsWith(allow, StringComparison.OrdinalIgnoreCase))
                    {
                        allowed = true;
                        break;
                    }
                }

                if (!allowed)
                {
                    foreach (var prefix in DenyForChild)
                    {
                        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            context.Response.ContentType = "application/json; charset=utf-8";
                            await context.Response.WriteAsync(
                                "{\"success\":false,\"message\":\"غير مصرّح لحساب الطفل بالوصول إلى هذا المسار.\",\"errors\":[{\"code\":\"child_scope_blocked\",\"message\":\"غير مصرّح.\"}]}",
                                context.RequestAborted).ConfigureAwait(false);
                            return;
                        }
                    }
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}

public static class ScopeEnforcementMiddlewareExtensions
{
    public static IApplicationBuilder UseScopeEnforcement(this IApplicationBuilder app)
        => app.UseMiddleware<ScopeEnforcementMiddleware>();
}
