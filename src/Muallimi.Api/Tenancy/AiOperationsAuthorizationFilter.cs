using Microsoft.AspNetCore.Mvc.Filters;

namespace Muallimi.Api.Tenancy;

/// <summary>
/// Enforces operator-only access to the AI operations query surface. Only
/// the `incident_investigation` role may see the minimal `question_text`
/// preview — every other operator sees redacted content.
/// </summary>
public class AiOperationsAuthorizationFilter : IAsyncActionFilter
{
    public const string OperatorRole = "operator";
    public const string IncidentInvestigationRole = "incident_investigation";
    public const string RoleItemKey = "ai_ops_role";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var role = http.Request.Headers["X-Actor-Role"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(role) || (role != OperatorRole && role != IncidentInvestigationRole))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = "Operator role required." });
            return;
        }
        http.Items[RoleItemKey] = role;
        await next();
    }
}
