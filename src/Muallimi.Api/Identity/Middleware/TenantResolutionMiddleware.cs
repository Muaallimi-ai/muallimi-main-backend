using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Muallimi.Application.Identity.Services;

namespace Muallimi.Api.Identity.Middleware;

/// <summary>
/// T039 — Reads the effective tenant for the current HTTP request and
/// caches the result on <c>HttpContext.Items</c> so
/// <see cref="IdentityAwareTenantContextAccessor"/> can expose it to
/// EF Core's global query filter without re-parsing the JWT or the
/// override header on every query.
///
/// The resolution flow:
/// <list type="number">
///   <item><description>If the request carries a validated JWT, the
///     <c>tenant_id</c> claim wins.</description></item>
///   <item><description>If a caller with super-admin / platform-operator
///     role sends <c>X-Tenant-Override</c>, the override wins — the
///     caller is responsible for emitting an audit event.</description></item>
///   <item><description>Otherwise the existing Phase-3 <c>X-Tenant-Id</c>
///     header is honoured so integration tests and legacy flows keep
///     working until every endpoint is migrated to JWT auth.</description></item>
/// </list>
/// </summary>
public sealed class TenantResolutionMiddleware
{
    public const string ResolvedTenantItemKey = "identity.resolved_tenant_id";
    public const string TenantOverrideHeader = "X-Tenant-Override";
    public const string LegacyTenantHeader = "X-Tenant-Id";

    private readonly RequestDelegate _next;
    private readonly ITenantResolutionService _resolver;

    public TenantResolutionMiddleware(RequestDelegate next, ITenantResolutionService resolver)
    {
        _next = next;
        _resolver = resolver;
    }

    public Task InvokeAsync(HttpContext context)
    {
        Guid? resolved = null;
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var overrideHeader = context.Request.Headers[TenantOverrideHeader].FirstOrDefault();
            var resolution = _resolver.Resolve(context.User, overrideHeader);
            resolved = resolution.TenantId;
        }
        if (resolved is null)
        {
            var legacyHeader = context.Request.Headers[LegacyTenantHeader].FirstOrDefault();
            if (Guid.TryParse(legacyHeader, out var legacy))
            {
                resolved = legacy;
            }
        }
        if (resolved is not null)
        {
            context.Items[ResolvedTenantItemKey] = resolved.Value;
        }
        return _next(context);
    }
}

public static class TenantResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseIdentityTenantResolution(this IApplicationBuilder app)
        => app.UseMiddleware<TenantResolutionMiddleware>();
}
