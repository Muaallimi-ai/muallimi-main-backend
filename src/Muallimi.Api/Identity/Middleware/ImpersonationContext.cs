using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Identity.Middleware;

/// <summary>
/// T156 — Reads the <c>impersonating.session</c> value from the JWT
/// claim and surfaces it as <see cref="CurrentSessionId"/> on
/// <c>HttpContext.Items</c> so every downstream service — in particular
/// the <c>AuditEventEmitter</c> helpers — can tag audit events with the
/// active impersonation session without re-parsing the token.
///
/// The claim is a JSON object: <c>{ "by": "...", "session": "...",
/// "expires_at": "..." }</c>. Absent or empty claim means no active
/// impersonation.
/// </summary>
public sealed class ImpersonationContextMiddleware
{
    public const string ItemKey = "identity.impersonation_session_id";

    private readonly RequestDelegate _next;

    public ImpersonationContextMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var raw = context.User.FindFirst("impersonating")?.Value;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("session", out var sessionEl) &&
                        !string.IsNullOrWhiteSpace(sessionEl.GetString()))
                    {
                        context.Items[ItemKey] = sessionEl.GetString()!;
                    }
                }
                catch (JsonException) { }
            }
        }
        return _next(context);
    }
}

/// <summary>
/// Provides the current impersonation session ID to services that need
/// to tag audit events. Resolved from <c>HttpContext.Items</c> set by
/// <see cref="ImpersonationContextMiddleware"/>.
/// </summary>
public interface IImpersonationContext
{
    string? CurrentSessionId { get; }
}

/// <summary>
/// HTTP-request-scoped implementation reading from
/// <see cref="IHttpContextAccessor"/>.
/// </summary>
public sealed class HttpImpersonationContext : IImpersonationContext
{
    private readonly IHttpContextAccessor _accessor;

    public HttpImpersonationContext(IHttpContextAccessor accessor)
        => _accessor = accessor;

    public string? CurrentSessionId =>
        _accessor.HttpContext?.Items.TryGetValue(ImpersonationContextMiddleware.ItemKey, out var v) == true
            ? v as string
            : null;
}

public static class ImpersonationContextExtensions
{
    public static Microsoft.AspNetCore.Builder.IApplicationBuilder UseImpersonationContext(
        this Microsoft.AspNetCore.Builder.IApplicationBuilder app)
        => app.UseMiddleware<ImpersonationContextMiddleware>();
}
