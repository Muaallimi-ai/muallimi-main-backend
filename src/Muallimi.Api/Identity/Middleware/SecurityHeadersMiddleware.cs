using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Identity.Middleware;

/// <summary>
/// T038 — Baseline security headers for every response served by the
/// Identity endpoints (and, wired globally in <c>Program.cs</c>, for the
/// whole backend). HSTS is declared even under HTTP so upstream proxies
/// pass it through; CSP is deliberately permissive ("default-src 'self'")
/// because this host serves only JSON — the tight CSP belongs to the
/// frontend origin.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Content-Security-Policy"] = "default-src 'self'";
        headers["X-Frame-Options"] = "DENY";
        headers["Permissions-Policy"] = "geolocation=(), microphone=(self), camera=()";
        return _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseIdentitySecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
