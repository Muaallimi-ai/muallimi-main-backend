using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Identity.Filters;

namespace Muallimi.Api.Identity.Startup;

/// <summary>
/// T043 — Root route group for every Phase 9 Identity endpoint. User
/// stories (US1 public auth, US2 parent-children, US3 admin) will
/// register their own <c>MapGroup("...")</c> children inside this group
/// so they inherit CORS + authorization wiring in one place.
///
/// This Part 3 scaffold only exposes a version probe so the frontend
/// and the smoke script can confirm the module is wired without
/// running the full auth flow.
/// </summary>
public static class IdentityEndpointRouteBuilderExtensions
{
    public const string IdentityRoutePrefix = "/api/auth";

    public static RouteGroupBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(IdentityRoutePrefix)
            .WithTags("Identity")
            .RequireCors(IdentityServiceCollectionExtensions.IdentityCorsPolicy)
            .AddEndpointFilter<IdentityAuthorizationFilter>();

        // Health probe. Does NOT require auth — intentional. Smoke
        // script uses this to verify the module is mapped before the
        // first /register + /login.
        group.MapGet("/_module", () => Results.Ok(new
        {
            module = "identity",
            phase = 9,
            status = "us1",
        }));

        // US1: public auth endpoints + authenticated session endpoints.
        group.MapPublicAuthEndpoints();
        group.MapAuthenticatedEndpoints();

        // US2: parent-children management.
        group.MapParentChildrenEndpoints();

        // US3: admin user management + forced-rotation gate + invitation accept.
        group.MapAdminUserEndpoints();

        return group;
    }
}
