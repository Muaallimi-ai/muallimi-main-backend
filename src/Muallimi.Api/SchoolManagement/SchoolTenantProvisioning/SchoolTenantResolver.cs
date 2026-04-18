using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;

/// <summary>
/// T017 — <c>SchoolTenantResolver</c>.
///
/// Resolves the active <c>school_tenant_id</c> for the current request from
/// the authenticated user's claims. Every school-scoped endpoint uses this
/// resolver so the data layer can scope queries by <c>school_tenant_id</c> in
/// addition to the ambient <c>tenant_id</c>.
///
/// Claim lookup order:
///   1. <c>school_admin:{school_tenant_id}</c>
///   2. <c>teacher:{school_tenant_id}:{class_id}:{subject_id}</c>
///   3. <c>X-School-Tenant-Id</c> header (operator-impersonation path only)
/// </summary>
public interface ISchoolTenantResolver
{
    Task<Guid?> ResolveAsync(CancellationToken ct = default);
}

public sealed class SchoolTenantResolver : ISchoolTenantResolver
{
    private readonly IHttpContextAccessor _http;
    private readonly MuallimiDbContext _db;

    public SchoolTenantResolver(IHttpContextAccessor http, MuallimiDbContext db)
    {
        _http = http;
        _db = db;
    }

    public Task<Guid?> ResolveAsync(CancellationToken ct = default)
    {
        var ctx = _http.HttpContext;
        if (ctx is null)
        {
            return Task.FromResult<Guid?>(null);
        }

        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var adminClaim = ctx.User.Claims
                .FirstOrDefault(c => c.Type.StartsWith("school_admin:", StringComparison.Ordinal));
            if (adminClaim is not null
                && Guid.TryParse(adminClaim.Type.AsSpan("school_admin:".Length), out var adminSchool))
            {
                return Task.FromResult<Guid?>(adminSchool);
            }

            var teacherClaim = ctx.User.Claims
                .FirstOrDefault(c => c.Type.StartsWith("teacher:", StringComparison.Ordinal));
            if (teacherClaim is not null)
            {
                var parts = teacherClaim.Type.Split(':');
                if (parts.Length >= 2 && Guid.TryParse(parts[1], out var teacherSchool))
                {
                    return Task.FromResult<Guid?>(teacherSchool);
                }
            }
        }

        if (ctx.Request.Headers.TryGetValue("X-School-Tenant-Id", out var headerValue)
            && Guid.TryParse(headerValue.ToString(), out var headerSchool))
        {
            return Task.FromResult<Guid?>(headerSchool);
        }

        return Task.FromResult<Guid?>(null);
    }
}

public static class SchoolTenantResolverServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolTenantResolver(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ISchoolTenantResolver, SchoolTenantResolver>();
        return services;
    }
}
