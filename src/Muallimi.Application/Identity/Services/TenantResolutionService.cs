using System;
using System.Linq;
using System.Security.Claims;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T034 — Resolves the effective tenant for the current request.
/// Normal path reads <c>tenant_id</c> from the JWT. Super-admins (and
/// platform-operators during authorized impersonation) may supply an
/// <c>X-Tenant-Override</c> header to query inside another tenant;
/// the resolver validates the role before honouring the override and
/// the caller is responsible for emitting an audit event.
/// </summary>
public interface ITenantResolutionService
{
    TenantResolution Resolve(ClaimsPrincipal principal, string? tenantOverrideHeader);
}

public sealed record TenantResolution(
    Guid? TenantId,
    bool IsOverride,
    string? DenyReason);

public sealed class TenantResolutionService : ITenantResolutionService
{
    private static readonly string[] OverrideAllowedRoles = { "super-admin", "platform-operator" };

    public TenantResolution Resolve(ClaimsPrincipal principal, string? tenantOverrideHeader)
    {
        var claimTenantRaw = principal.FindFirst("tenant_id")?.Value;
        Guid.TryParse(claimTenantRaw, out var claimTenant);

        if (string.IsNullOrWhiteSpace(tenantOverrideHeader))
        {
            if (claimTenant == Guid.Empty)
            {
                return new TenantResolution(null, false, "no_tenant_claim");
            }
            return new TenantResolution(claimTenant, false, null);
        }

        if (!Guid.TryParse(tenantOverrideHeader, out var overrideTenant) || overrideTenant == Guid.Empty)
        {
            return new TenantResolution(null, false, "invalid_override_header");
        }

        var roleClaims = principal.FindAll("roles").Select(c => c.Value);
        if (!roleClaims.Any(r => OverrideAllowedRoles.Contains(r, StringComparer.Ordinal)))
        {
            return new TenantResolution(null, false, "override_forbidden");
        }

        return new TenantResolution(overrideTenant, true, null);
    }
}
