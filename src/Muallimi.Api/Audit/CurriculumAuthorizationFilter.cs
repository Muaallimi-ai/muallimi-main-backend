using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Muallimi.Api.Audit;

/// <summary>
/// Authorization filter that enforces role and tenant access for curriculum admin endpoints.
/// Only CurriculumAdmin, SubjectExpert, and PlatformOperator roles are allowed.
/// </summary>
public class CurriculumAuthorizationFilter : IAsyncAuthorizationFilter
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "curriculum-admin",
        "subject-expert",
        "platform-operator"
    };

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Extract role from claims (Phase 0 identity baseline)
        var roleClaim = context.HttpContext.User.FindFirst("role")?.Value
                     ?? context.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;

        if (string.IsNullOrEmpty(roleClaim) || !AllowedRoles.Contains(roleClaim))
        {
            context.Result = new ForbidResult();
            return Task.CompletedTask;
        }

        // Extract tenant from claims
        var tenantId = context.HttpContext.User.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrEmpty(tenantId))
        {
            context.Result = new UnauthorizedResult();
            return Task.CompletedTask;
        }

        // Store tenant in HttpContext for downstream use
        context.HttpContext.Items["TenantId"] = tenantId;
        context.HttpContext.Items["ActorRole"] = roleClaim;

        return Task.CompletedTask;
    }
}

/// <summary>
/// Attribute to apply curriculum authorization to controllers or actions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class CurriculumAuthorizeAttribute : TypeFilterAttribute
{
    public CurriculumAuthorizeAttribute() : base(typeof(CurriculumAuthorizationFilter)) { }
}
