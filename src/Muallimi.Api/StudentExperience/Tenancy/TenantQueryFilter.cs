using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Shared;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.Tenancy;

/// <summary>
/// T007 — Tenant resolution for Phase 3. The heavy-lifting query filter is
/// wired in <see cref="MuallimiDbContext"/> against <see cref="ITenantScoped"/>
/// entities; this file only provides the ambient accessor so the DbContext
/// knows which tenant the current HTTP request targets.
///
/// Constitution rule: every query is tenant-scoped; cross-tenant access is
/// always denied. Requests without an <c>X-Tenant-Id</c> header get a null
/// tenant, which forces the global filter to match nothing.
/// </summary>
public sealed class HttpTenantContextAccessor : IDbTenantContextAccessor
{
    public const string TenantHeaderName = "X-Tenant-Id";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpTenantContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? CurrentTenantId
    {
        get
        {
            var raw = _httpContextAccessor.HttpContext?.Request.Headers[TenantHeaderName].ToString();
            return Guid.TryParse(raw, out var tenantId) ? tenantId : null;
        }
    }
}

public static class TenantQueryFilterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase3Tenancy(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IDbTenantContextAccessor, HttpTenantContextAccessor>();
        return services;
    }
}
