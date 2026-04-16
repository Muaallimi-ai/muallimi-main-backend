using Microsoft.AspNetCore.Mvc.Filters;

namespace Muallimi.Api.Tenancy;

/// <summary>
/// Enforces tenant isolation and session scope for the student-facing tutor
/// endpoint. A tutor request must carry `X-Tenant-Id` and `X-Session-Id`
/// headers matching a live Study session for the authenticated student.
/// </summary>
public class TutorAuthorizationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var tenantId = http.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        var sessionId = http.Request.Headers["X-Session-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(sessionId))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await http.Response.WriteAsJsonAsync(new { error = "Tenant or session scope missing." });
            return;
        }
        http.Items["tenant_id"] = tenantId;
        http.Items["session_id"] = sessionId;
        await next();
    }
}
