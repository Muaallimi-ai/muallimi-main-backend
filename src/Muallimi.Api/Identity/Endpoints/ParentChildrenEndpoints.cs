using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;

namespace Muallimi.Api.Identity.Endpoints;

/// <summary>
/// T092 — Parent-children endpoints. Mounted inside the identity group
/// under <c>/api/auth/parent/children</c>. Every route requires a valid
/// parent JWT — the handler validates the bearer token via
/// <see cref="AuthClaimsReader"/> (same pattern as <c>/logout</c> and
/// <c>/me</c>), then delegates to <see cref="IUserManagementService"/>.
///
/// The <c>POST /</c> and <c>POST /{id}/regenerate-password</c> handlers
/// are the only ones that ever emit a plaintext password in their
/// response; every other read path returns <see cref="ChildSummary"/>
/// or <see cref="ChildDetail"/>, neither of which carry the password.
/// </summary>
public static class ParentChildrenEndpoints
{
    public const string GroupRoute = "/parent/children";
    public const string RegenerateSubRoute = "/{id:guid}/regenerate-password";

    public const string SuspendSubRoute = "/{id:guid}/suspend";
    public const string UnsuspendSubRoute = "/{id:guid}/unsuspend";
    public const string SessionsSubRoute = "/{id:guid}/sessions";
    public const string LoginHistorySubRoute = "/{id:guid}/login-history";

    public static RouteGroupBuilder MapParentChildrenEndpoints(this RouteGroupBuilder parent)
    {
        parent.MapPost(GroupRoute, HandleCreateChildAsync);
        parent.MapGet(GroupRoute, HandleListChildrenAsync);
        parent.MapGet(GroupRoute + "/{id:guid}", HandleGetChildAsync);
        parent.MapPatch(GroupRoute + "/{id:guid}", HandleUpdateChildAsync);
        parent.MapPost(GroupRoute + RegenerateSubRoute, HandleRegenerateAsync);
        parent.MapDelete(GroupRoute + "/{id:guid}", HandleDeleteChildAsync);

        // US5 — Parent oversight
        parent.MapPost(GroupRoute + SuspendSubRoute, HandleSuspendChildAsync);
        parent.MapPost(GroupRoute + UnsuspendSubRoute, HandleUnsuspendChildAsync);
        parent.MapGet(GroupRoute + SessionsSubRoute, HandleListChildSessionsAsync);
        parent.MapDelete(GroupRoute + "/{id:guid}/sessions/{sessionId:guid}", HandleRevokeChildSessionAsync);
        parent.MapGet(GroupRoute + LoginHistorySubRoute, HandleGetLoginHistoryAsync);
        return parent;
    }

    private static async Task<IResult> HandleCreateChildAsync(
        CreateChildRequest request,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<CreateChildCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
        {
            return unauthorized!;
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }

        var cmd = new CreateChildCommand(
            ParentUserId: claims.UserId,
            ParentTenantId: claims.TenantId,
            FullName: request.FullName ?? string.Empty,
            FullNameEn: request.FullNameEn,
            Grade: request.Grade,
            Gender: (request.Gender ?? string.Empty).Trim().ToLowerInvariant(),
            Birthday: request.Birthday,
            PreferredUsername: request.PreferredUsername,
            CustomPassword: request.CustomPassword,
            PasswordLocale: string.IsNullOrWhiteSpace(request.PasswordLocale) ? "ar" : request.PasswordLocale!.Trim().ToLowerInvariant(),
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }

        var result = await users.CreateChildAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleListChildrenAsync(
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
        {
            return unauthorized!;
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }
        var list = await users.ListChildrenAsync(claims.UserId, claims.TenantId, ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { children = list }, "ok", correlationId);
    }

    private static async Task<IResult> HandleGetChildAsync(
        Guid id,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
        {
            return unauthorized!;
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }
        var child = await users.GetChildAsync(claims.UserId, id, ct).ConfigureAwait(false);
        if (child is null)
        {
            return AuthEndpointHelpers.FailEnvelope(404, "child_not_found", "الطفل غير موجود.", correlationId);
        }
        return AuthEndpointHelpers.OkEnvelope(child, "ok", correlationId);
    }

    private static async Task<IResult> HandleUpdateChildAsync(
        Guid id,
        UpdateChildRequest request,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<UpdateChildCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
        {
            return unauthorized!;
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }
        var cmd = new UpdateChildCommand(
            ParentUserId: claims.UserId,
            ParentTenantId: claims.TenantId,
            ChildUserId: id,
            FullName: request.FullName,
            FullNameEn: request.FullNameEn,
            Grade: request.Grade,
            Gender: request.Gender?.Trim().ToLowerInvariant(),
            Birthday: request.Birthday,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var result = await users.UpdateChildAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleRegenerateAsync(
        Guid id,
        RegenerateChildPasswordRequest? request,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<RegenerateChildPasswordCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
        {
            return unauthorized!;
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }
        var body = request ?? new RegenerateChildPasswordRequest();
        var cmd = new RegenerateChildPasswordCommand(
            ParentUserId: claims.UserId,
            ParentTenantId: claims.TenantId,
            ChildUserId: id,
            CustomPassword: body.CustomPassword,
            PasswordLocale: string.IsNullOrWhiteSpace(body.PasswordLocale) ? "ar" : body.PasswordLocale!.Trim().ToLowerInvariant(),
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var result = await users.RegenerateChildPasswordAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleDeleteChildAsync(
        Guid id,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<DeleteChildCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
        {
            return unauthorized!;
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }
        var cmd = new DeleteChildCommand(
            ParentUserId: claims.UserId,
            ParentTenantId: claims.TenantId,
            ChildUserId: id,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var result = await users.DeleteChildAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    // ── US5 handlers ──────────────────────────────────────────────────────

    private static async Task<IResult> HandleSuspendChildAsync(
        Guid id,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<SuspendChildCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
            return unauthorized!;
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var cmd = new SuspendChildCommand(
            claims.UserId, claims.TenantId, id,
            AuthEndpointHelpers.ResolveIp(http),
            AuthEndpointHelpers.ResolveUserAgent(http),
            correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var result = await users.SuspendChildAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleUnsuspendChildAsync(
        Guid id,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<UnsuspendChildCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
            return unauthorized!;
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var cmd = new UnsuspendChildCommand(
            claims.UserId, claims.TenantId, id,
            AuthEndpointHelpers.ResolveIp(http),
            AuthEndpointHelpers.ResolveUserAgent(http),
            correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var result = await users.UnsuspendChildAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleListChildSessionsAsync(
        Guid id,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
            return unauthorized!;
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var list = await users.ListChildSessionsAsync(claims.UserId, id, ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { sessions = list }, "ok", correlationId);
    }

    private static async Task<IResult> HandleRevokeChildSessionAsync(
        Guid id,
        Guid sessionId,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<RevokeChildSessionCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
            return unauthorized!;
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var cmd = new RevokeChildSessionCommand(
            claims.UserId, claims.TenantId, id, sessionId,
            AuthEndpointHelpers.ResolveIp(http),
            AuthEndpointHelpers.ResolveUserAgent(http),
            correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var result = await users.RevokeChildSessionAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleGetLoginHistoryAsync(
        Guid id,
        HttpContext http,
        IUserManagementService users,
        ITokenService tokens,
        ISessionService sessions,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!TryRequireParent(http, tokens, correlationId, out var claims, out var unauthorized))
            return unauthorized!;
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var history = await users.GetChildLoginHistoryAsync(claims.UserId, id, 50, ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { history }, "ok", correlationId);
    }

    private static bool TryRequireParent(
        HttpContext http,
        ITokenService tokens,
        string correlationId,
        out AuthClaims claims,
        out IResult? unauthorized)
    {
        if (!AuthClaimsReader.TryExtract(http, tokens, out claims, out var reason))
        {
            unauthorized = AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
            return false;
        }
        if (!claims.Roles.Any(r => string.Equals(r, "parent", StringComparison.Ordinal)))
        {
            unauthorized = AuthEndpointHelpers.FailEnvelope(403, "role_insufficient", "الدور غير كافٍ.", correlationId);
            return false;
        }
        unauthorized = null;
        return true;
    }

    private static IResult RenderResult<T>(ChildOperationResult<T> result, string correlationId)
    {
        if (result.Success)
        {
            return AuthEndpointHelpers.StatusEnvelope(result.HttpStatus, result.Payload, result.Message, correlationId);
        }
        return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "operation_failed", result.Message, correlationId, result.Errors);
    }
}
