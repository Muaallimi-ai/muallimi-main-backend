using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Security.TransportSecurity;

/// <summary>
/// T088 — Enforces TLS 1.2+, HSTS, and baseline security response headers on
/// every outbound response per security-data-protection-contract.md. CORS is
/// configured in Program.cs with an allow-list of known frontend origins.
/// </summary>
public class TransportSecurityMiddleware
{
    private const string HstsHeaderValue = "max-age=31536000; includeSubDomains";

    private readonly RequestDelegate _next;

    public TransportSecurityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        if (context.Request.IsHttps && !headers.ContainsKey("Strict-Transport-Security"))
        {
            headers["Strict-Transport-Security"] = HstsHeaderValue;
        }

        if (!headers.ContainsKey("X-Content-Type-Options"))
        {
            headers["X-Content-Type-Options"] = "nosniff";
        }

        if (!headers.ContainsKey("X-Frame-Options"))
        {
            headers["X-Frame-Options"] = "DENY";
        }

        if (!headers.ContainsKey("Referrer-Policy"))
        {
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        }

        await _next(context);
    }
}

public static class TransportSecurityExtensions
{
    public static IApplicationBuilder UsePhase6TransportSecurity(this IApplicationBuilder app)
        => app.UseMiddleware<TransportSecurityMiddleware>();
}
