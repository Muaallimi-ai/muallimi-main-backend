using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Identity.Filters;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;

namespace Muallimi.Api.Identity.Endpoints;

/// <summary>
/// T157 — US6 operator impersonation endpoints mounted under
/// <c>/api/auth/admin</c>.
///
/// POST /admin/impersonate      — super-admin / platform-operator starts
///                               a 1-hour impersonation session.
/// POST /admin/impersonate/end  — terminates the active session.
///
/// Both endpoints require a platform role (super-admin or
/// platform-operator). The bearer token carries the <c>roles</c> claim
/// set by the login/refresh path; the filter reads it via
/// <see cref="IdentityAuthorizationFilter"/>.
/// </summary>
public static class AdminImpersonationEndpoints
{
    public const string StartRoute = "/impersonate";
    public const string EndRoute = "/impersonate/end";

    public static RouteGroupBuilder MapAdminImpersonationEndpoints(this RouteGroupBuilder adminGroup)
    {
        adminGroup.MapPost(StartRoute, HandleStartAsync)
            .RequireRole("super-admin", "platform-operator");
        adminGroup.MapPost(EndRoute, HandleEndAsync)
            .RequireRole("super-admin", "platform-operator");
        return adminGroup;
    }

    // ── Handlers ───────────────────────────────────────────────────────

    private static async Task<IResult> HandleStartAsync(
        HttpContext http,
        ITokenService tokens,
        IImpersonationService impersonation,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
        {
            AuthEndpointHelpers.EchoCorrelation(http, correlationId);
            return Results.Unauthorized();
        }

        ImpersonateRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ImpersonateRequest>(ct)
                .ConfigureAwait(false);
        }
        catch
        {
            request = null;
        }

        if (request is null || request.TargetUserId == Guid.Empty)
        {
            AuthEndpointHelpers.EchoCorrelation(http, correlationId);
            return Results.BadRequest(ApiResponseEnvelope<object>.Fail(
                "targetUserId is required.", Array.Empty<ApiResponseError>(), correlationId));
        }

        var cmd = new StartImpersonationCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            TargetUserId: request.TargetUserId,
            Reason: request.Reason ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);

        var result = await impersonation.StartAsync(cmd, ct).ConfigureAwait(false);
        AuthEndpointHelpers.EchoCorrelation(http, correlationId);

        if (!result.Success)
        {
            return AuthEndpointHelpers.StatusEnvelope<object>(result.HttpStatus, null, result.Message, correlationId);
        }

        return AuthEndpointHelpers.OkEnvelope(result.Payload!, result.Message, correlationId);
    }

    private static async Task<IResult> HandleEndAsync(
        HttpContext http,
        ITokenService tokens,
        IImpersonationService impersonation,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
        {
            AuthEndpointHelpers.EchoCorrelation(http, correlationId);
            return Results.Unauthorized();
        }

        EndImpersonationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<EndImpersonationRequest>(ct)
                .ConfigureAwait(false);
        }
        catch
        {
            request = null;
        }

        if (request is null || request.ImpersonationSessionId == Guid.Empty)
        {
            AuthEndpointHelpers.EchoCorrelation(http, correlationId);
            return Results.BadRequest(ApiResponseEnvelope<object>.Fail(
                "impersonationSessionId is required.", Array.Empty<ApiResponseError>(), correlationId));
        }

        var cmd = new EndImpersonationCommand(
            ActorUserId: claims.UserId,
            ImpersonationSessionId: request.ImpersonationSessionId,
            CorrelationId: correlationId);

        var result = await impersonation.EndAsync(cmd, ct).ConfigureAwait(false);
        AuthEndpointHelpers.EchoCorrelation(http, correlationId);

        if (!result.Success)
        {
            return AuthEndpointHelpers.StatusEnvelope<object>(result.HttpStatus, null, result.Message, correlationId);
        }

        return AuthEndpointHelpers.OkEnvelope<object>(new { ended = true }, result.Message, correlationId);
    }
}

public sealed class ImpersonateRequest
{
    [JsonPropertyName("targetUserId")]
    public Guid TargetUserId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public sealed class EndImpersonationRequest
{
    [JsonPropertyName("impersonationSessionId")]
    public Guid ImpersonationSessionId { get; set; }
}
