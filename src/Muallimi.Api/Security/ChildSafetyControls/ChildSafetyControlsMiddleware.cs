using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Security.ChildSafetyControls;

/// <summary>
/// T089 — Enforces child-safety controls on external communication bindings.
/// The middleware blocks attempts to bind a student (child) account directly
/// to an email, push, or WhatsApp channel without parental consent — external
/// channels always flow through the <see cref="Muallimi.Domain.Parents"/>
/// ParentProfile relationship. Inbound requests are inspected for child actor
/// identifiers and external-channel binding endpoints; violations short-circuit
/// with 403 child_safety_violation.
/// </summary>
public class ChildSafetyControlsMiddleware
{
    private static readonly string[] ExternalChannelPaths =
    {
        "/api/v1/notifications/bindings",
        "/api/v1/notifications/subscribe",
        "/api/v1/notifications/email",
        "/api/v1/notifications/push",
        "/api/v1/notifications/whatsapp",
    };

    private static readonly string[] ExternalChannelNames =
    {
        "email", "push", "whatsapp"
    };

    private readonly RequestDelegate _next;

    public ChildSafetyControlsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method)
            && IsExternalChannelPath(context.Request.Path)
            && IsChildActor(context)
            && !HasParentalConsent(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "child_safety_violation",
                message = "Direct external channel bindings are not permitted for child accounts without parental consent."
            });
            return;
        }

        await _next(context);
    }

    private static bool IsExternalChannelPath(PathString path)
    {
        if (!path.HasValue) return false;
        var p = path.Value!;
        foreach (var candidate in ExternalChannelPaths)
        {
            if (p.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // Also match query-param or path-embedded channel names like
        // /notifications/channels/email for safety.
        foreach (var channel in ExternalChannelNames)
        {
            if (p.Contains("/notifications/", StringComparison.OrdinalIgnoreCase)
                && p.Contains("/" + channel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsChildActor(HttpContext context)
    {
        var actorType = context.Request.Headers["X-Actor-Type"].FirstOrDefault();
        return string.Equals(actorType, "student", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasParentalConsent(HttpContext context)
    {
        var consent = context.Request.Headers["X-Parental-Consent"].FirstOrDefault();
        return string.Equals(consent, "granted", StringComparison.OrdinalIgnoreCase);
    }
}

public static class ChildSafetyControlsExtensions
{
    public static IApplicationBuilder UsePhase6ChildSafetyControls(this IApplicationBuilder app)
        => app.UseMiddleware<ChildSafetyControlsMiddleware>();
}
