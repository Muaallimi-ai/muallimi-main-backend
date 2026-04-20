using System;
using System.Collections.Generic;
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
using Muallimi.Application.Identity.Queries;
using Muallimi.Application.Identity.Services;

namespace Muallimi.Api.Identity.Endpoints;

/// <summary>
/// T111 — Admin user-management endpoints (Phase 9 US3). Mounted under
/// <c>/api/auth/admin</c>. Super-admin has write access; platform-operator
/// is restricted to GETs by <see cref="RequireSuperAdminAttribute"/>
/// vs <see cref="RequirePlatformRoleAttribute"/>.
/// </summary>
public static class AdminUserEndpoints
{
    public const string GroupRoute = "/admin";

    public static RouteGroupBuilder MapAdminUserEndpoints(this RouteGroupBuilder parent)
    {
        var admin = parent.MapGroup(GroupRoute);

        admin.MapGet("/users", HandleListUsersAsync).RequirePlatformRole();
        admin.MapGet("/users/{id:guid}", HandleGetUserAsync).RequirePlatformRole();
        admin.MapPost("/users/invite", HandleInviteAsync).RequireSuperAdmin();
        admin.MapPost("/users/{id:guid}/roles", HandleGrantRoleAsync).RequireSuperAdmin();
        admin.MapDelete("/users/{id:guid}/roles/{roleName}", HandleRevokeRoleAsync).RequireSuperAdmin();
        admin.MapPost("/users/{id:guid}/suspend", HandleSuspendAsync).RequireSuperAdmin();
        admin.MapPost("/users/{id:guid}/unsuspend", HandleUnsuspendAsync).RequireSuperAdmin();
        admin.MapDelete("/users/{id:guid}", HandleDeleteAsync).RequireSuperAdmin();
        admin.MapPost("/users/{id:guid}/reset-password", HandleAdminResetAsync).RequireSuperAdmin();
        admin.MapGet("/roles", HandleListRolesAsync).RequirePlatformRole();
        admin.MapGet("/audit", HandleAuditAsync).RequirePlatformRole();

        // US6 T157 — impersonation endpoints wired on the same /admin group.
        admin.MapAdminImpersonationEndpoints();

        // Invitation acceptance (T121) — public, token-gated.
        parent.MapPost("/invitation/accept", HandleAcceptInvitationAsync);
        return parent;
    }

    // ── Handlers ───────────────────────────────────────────────────────

    private static async Task<IResult> HandleListUsersAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        int? page,
        int? pageSize,
        Guid? tenantId,
        string? role,
        string? status,
        string? q,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var result = await admin.ListUsersAsync(new ListUsersQuery(
            ActorUserId: auth.Claims.UserId,
            ActorRoles: auth.Claims.Roles,
            TenantId: tenantId,
            RoleName: role,
            Status: status,
            Search: q,
            Page: page ?? 1,
            PageSize: pageSize ?? 50), ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleGetUserAsync(
        Guid id,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var result = await admin.GetUserAsync(id, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleInviteAsync(
        InviteUserRequest request,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        ICommandValidator<InviteUserCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var claims = auth.Claims;
        var cmd = new InviteUserCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            ActorRoles: claims.Roles,
            Email: request.Email ?? string.Empty,
            FullName: request.FullName ?? string.Empty,
            FullNameEn: request.FullNameEn,
            Locale: string.IsNullOrWhiteSpace(request.Locale) ? "ar" : request.Locale!,
            Roles: request.Roles ?? Array.Empty<string>(),
            TargetTenantId: request.TenantId,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.InviteUserAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleGrantRoleAsync(
        Guid id,
        GrantRoleRequest request,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        ICommandValidator<GrantRoleCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var claims = auth.Claims;
        var cmd = new GrantRoleCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            ActorRoles: claims.Roles,
            TargetUserId: id,
            RoleName: (request.RoleName ?? string.Empty).Trim(),
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.GrantRoleAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleRevokeRoleAsync(
        Guid id,
        string roleName,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        ICommandValidator<RevokeRoleCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var claims = auth.Claims;
        var cmd = new RevokeRoleCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            ActorRoles: claims.Roles,
            TargetUserId: id,
            RoleName: (roleName ?? string.Empty).Trim(),
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.RevokeRoleAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleSuspendAsync(
        Guid id,
        SuspendUserRequest? request,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        ICommandValidator<SuspendUserCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var claims = auth.Claims;
        var cmd = new SuspendUserCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            ActorRoles: claims.Roles,
            TargetUserId: id,
            Reason: request?.Reason,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.SuspendUserAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleUnsuspendAsync(
        Guid id,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        ICommandValidator<UnsuspendUserCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var claims = auth.Claims;
        var cmd = new UnsuspendUserCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            ActorRoles: claims.Roles,
            TargetUserId: id,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.UnsuspendUserAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleDeleteAsync(
        Guid id,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        ICommandValidator<DeleteUserCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var claims = auth.Claims;
        var cmd = new DeleteUserCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            ActorRoles: claims.Roles,
            TargetUserId: id,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.DeleteUserAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleAdminResetAsync(
        Guid id,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        ICommandValidator<AdminResetPasswordCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var claims = auth.Claims;
        var cmd = new AdminResetPasswordCommand(
            ActorUserId: claims.UserId,
            ActorTenantId: claims.TenantId,
            ActorRoles: claims.Roles,
            TargetUserId: id,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.AdminResetPasswordAsync(cmd, ct).ConfigureAwait(false);
        return RenderResult(result, correlationId);
    }

    private static async Task<IResult> HandleListRolesAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAdminUserService admin,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var roles = await admin.ListRolesAsync(ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { roles }, "ok", correlationId);
    }

    private static async Task<IResult> HandleAuditAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IAuditLogQueryService auditQuery,
        Guid? tenantId,
        Guid? actorId,
        Guid? targetId,
        string? category,
        string? outcome,
        DateTime? from,
        DateTime? to,
        string? cursor,
        int? limit,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var auth = await RequireAdminAsync(http, tokens, sessions, correlationId, ct);
        if (!auth.Ok) return auth.Fail!;
        var page = await auditQuery.QueryAsync(new AuditLogQuery(
            TenantId: tenantId,
            ActorId: actorId,
            TargetId: targetId,
            Category: category,
            Outcome: outcome,
            From: from,
            To: to,
            Cursor: cursor,
            Limit: limit ?? 50), ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(page, "ok", correlationId);
    }

    // ── Forced-rotation gate & invitation accept ───────────────────────

    private static async Task<IResult> HandleAcceptInvitationAsync(
        AcceptInvitationRequest request,
        HttpContext http,
        IAdminUserService admin,
        ICommandValidator<AcceptInvitationCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new AcceptInvitationCommand(
            Token: request.Token ?? string.Empty,
            NewPassword: request.NewPassword ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await admin.AcceptInvitationAsync(cmd, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "invitation_failed", result.Message, correlationId, result.Errors);
        }
        var data = new
        {
            userId = result.Payload!.UserId.ToString("D"),
            tenantId = result.Payload.TenantId.ToString("D"),
            email = result.Payload.Email,
            fullName = result.Payload.FullName,
            roles = result.Payload.Roles,
        };
        return AuthEndpointHelpers.OkEnvelope(data, result.Message, correlationId);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<AdminAuthResolution> RequireAdminAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        string correlationId,
        CancellationToken ct)
    {
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
        {
            return new AdminAuthResolution(false, default, AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId));
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return new AdminAuthResolution(false, default, AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId));
        }
        return new AdminAuthResolution(true, claims, null);
    }

    private readonly record struct AdminAuthResolution(bool Ok, AuthClaims Claims, IResult? Fail);

    private static IResult RenderResult<T>(AdminOperationResult<T> result, string correlationId)
    {
        if (result.Success)
        {
            return AuthEndpointHelpers.StatusEnvelope(result.HttpStatus, result.Payload, result.Message, correlationId);
        }
        return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "operation_failed", result.Message, correlationId, result.Errors);
    }
}

public sealed class InviteUserRequest
{
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("fullName")] public string? FullName { get; set; }
    [JsonPropertyName("fullNameEn")] public string? FullNameEn { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("roles")] public IReadOnlyList<string>? Roles { get; set; }
    [JsonPropertyName("tenantId")] public Guid? TenantId { get; set; }
}

public sealed class GrantRoleRequest
{
    [JsonPropertyName("roleName")] public string? RoleName { get; set; }
}

public sealed class SuspendUserRequest
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class ChangePasswordRequest
{
    [JsonPropertyName("currentPassword")] public string? CurrentPassword { get; set; }
    [JsonPropertyName("newPassword")] public string? NewPassword { get; set; }
}

public sealed class AcceptInvitationRequest
{
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("newPassword")] public string? NewPassword { get; set; }
}
