using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Middleware;

/// <summary>
/// Add-child redesign Phase 5.4 — when a request carries a derived
/// child JWT (claim <c>derived_from_session_id</c>), touch the parent
/// session's <c>LastSeenAt</c> so the parent session does NOT idle-out
/// while the child is actively studying. Throttled to one DB write
/// per session per minute via <c>HttpContext.Items</c> — the cache
/// is process-local but that's fine: at worst we touch a couple of
/// times per minute across instances.
/// </summary>
public sealed class DerivedSessionKeepaliveMiddleware
{
    private const string ThrottleItemPrefix = "identity.parent_session_touched.";
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private readonly RequestDelegate _next;

    public DerivedSessionKeepaliveMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            var derivedRaw = context.User.FindFirst("derived_from_session_id")?.Value;
            if (!string.IsNullOrEmpty(derivedRaw) && Guid.TryParse(derivedRaw, out var parentSessionId))
            {
                var throttleKey = ThrottleItemPrefix + parentSessionId.ToString("D");
                if (!context.Items.ContainsKey(throttleKey))
                {
                    context.Items[throttleKey] = DateTime.UtcNow;
                    var db = context.RequestServices.GetService<MuallimiDbContext>();
                    if (db is not null)
                    {
                        try
                        {
                            await db.IdentityUserSessions.IgnoreQueryFilters()
                                .Where(s => s.Id == parentSessionId && s.RevokedAt == null
                                         && s.LastSeenAt < DateTime.UtcNow - TouchInterval)
                                .ExecuteUpdateAsync(
                                    setters => setters.SetProperty(x => x.LastSeenAt, DateTime.UtcNow),
                                    context.RequestAborted)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best-effort. Never fail the request because of a keep-alive miss.
                        }
                    }
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}

public static class DerivedSessionKeepaliveMiddlewareExtensions
{
    public static IApplicationBuilder UseDerivedSessionKeepalive(this IApplicationBuilder app)
        => app.UseMiddleware<DerivedSessionKeepaliveMiddleware>();
}
