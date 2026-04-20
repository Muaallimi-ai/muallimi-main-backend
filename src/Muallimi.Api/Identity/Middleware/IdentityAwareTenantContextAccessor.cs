using System;
using Microsoft.AspNetCore.Http;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Middleware;

/// <summary>
/// Replacement for <c>HttpTenantContextAccessor</c> that prefers the
/// resolved tenant cached on <c>HttpContext.Items</c> by
/// <see cref="TenantResolutionMiddleware"/>. Falls through to the legacy
/// <c>X-Tenant-Id</c> header when no resolved tenant is present — this
/// preserves compatibility with every Phase-1-6 integration test until
/// those tests migrate to issuing real JWTs.
/// </summary>
public sealed class IdentityAwareTenantContextAccessor : IDbTenantContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public IdentityAwareTenantContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? CurrentTenantId
    {
        get
        {
            var ctx = _httpContextAccessor.HttpContext;
            if (ctx is null) return null;

            if (ctx.Items.TryGetValue(TenantResolutionMiddleware.ResolvedTenantItemKey, out var cached) && cached is Guid resolved)
            {
                return resolved;
            }

            var raw = ctx.Request.Headers[TenantResolutionMiddleware.LegacyTenantHeader].ToString();
            return Guid.TryParse(raw, out var tenantId) ? tenantId : null;
        }
    }
}
