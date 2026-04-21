using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Muallimi.Api.Identity.Filters;

/// <summary>
/// T040 — Endpoint filters enforcing role-scoped authorization.
///
/// Identity endpoints (registered in Part 3's
/// <c>MapIdentityEndpoints</c>) compose these via the extension methods
/// below. The filter expects <c>HttpContext.User</c> to be populated by
/// the JWT-bearer middleware — a missing principal yields <c>401</c>,
/// an authenticated request without a matching role yields <c>403</c>.
///
/// The attribute-style public classes (<c>RequireRoleAttribute</c> etc.)
/// are kept so endpoint code can self-document required roles even
/// though minimal APIs use the <c>.RequireRole(...)</c> fluent calls at
/// registration time. Reading the attribute off <c>Endpoint.Metadata</c>
/// is how the shared filter (<see cref="IdentityAuthorizationFilter"/>)
/// decides whether a role gate applies.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public class RequireRoleAttribute : Attribute
{
    public IReadOnlyList<string> AcceptedRoles { get; }

    public RequireRoleAttribute(params string[] acceptedRoles)
    {
        if (acceptedRoles.Length == 0)
        {
            throw new ArgumentException("At least one role is required.", nameof(acceptedRoles));
        }
        AcceptedRoles = acceptedRoles;
    }
}

public sealed class RequireSuperAdminAttribute : RequireRoleAttribute
{
    public RequireSuperAdminAttribute() : base("super-admin") { }
}

public sealed class RequirePlatformRoleAttribute : RequireRoleAttribute
{
    public RequirePlatformRoleAttribute()
        : base("super-admin", "platform-operator", "curriculum-admin", "subject-expert") { }
}

/// <summary>
/// Endpoint filter that scans the endpoint's metadata for
/// <see cref="RequireRoleAttribute"/> entries and enforces them against
/// the request principal's <c>roles</c> claims.
/// </summary>
public sealed class IdentityAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var endpoint = http.GetEndpoint();
        var requirements = endpoint?.Metadata.GetOrderedMetadata<RequireRoleAttribute>() ?? Array.Empty<RequireRoleAttribute>();
        if (requirements.Count == 0)
        {
            return await next(context);
        }

        if (http.User.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        var userRoles = http.User.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            if (!requirement.AcceptedRoles.Any(r => userRoles.Contains(r)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
        }
        return await next(context);
    }
}

/// <summary>
/// Fluent helpers for endpoint authors. Each call adds the matching
/// <see cref="RequireRoleAttribute"/> to the route's metadata. The
/// shared <see cref="IdentityAuthorizationFilter"/> is attached once
/// at the group level in <c>MapIdentityEndpoints</c> and reads the
/// metadata on every request, so per-route fluent calls only need to
/// declare required roles.
/// </summary>
public static class IdentityAuthorizationBuilderExtensions
{
    public static TBuilder RequireRole<TBuilder>(this TBuilder builder, params string[] roles)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequireRoleAttribute(roles));
        return builder;
    }

    public static TBuilder RequireSuperAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireRole("super-admin");

    public static TBuilder RequirePlatformRole<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.RequireRole("super-admin", "platform-operator", "curriculum-admin", "subject-expert");
}
