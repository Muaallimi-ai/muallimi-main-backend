using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Endpoints;

/// <summary>
/// T076 + T134 — Endpoints that require a valid access token:
///   • POST /logout            (T076)
///   • GET  /me                (T076)
///   • POST /change-password   (T134 — replaces AdminUserEndpoints stub; adds session revocation)
///   • POST /forgot-password   (T134 — public, no token required)
///   • POST /reset-password    (T134 — public, token-gated)
///   • GET  /sessions          (T134)
///   • DELETE /sessions/{id}   (T134)
///   • DELETE /sessions        (T134)
///   • POST /2fa/enable        (T134)
///   • POST /2fa/verify        (T134)
///   • POST /2fa/disable       (T134)
/// </summary>
public static class AuthenticatedEndpoints
{
    public const string LogoutRoute = "/logout";
    public const string MeRoute = "/me";
    public const string ChangePasswordRoute = "/change-password";
    public const string ForgotPasswordRoute = "/forgot-password";
    public const string ResetPasswordRoute = "/reset-password";
    public const string SessionsRoute = "/sessions";
    public const string RevokeSessionRoute = "/sessions/{id:guid}";
    public const string RevokeAllSessionsRoute = "/sessions";  // DELETE verb
    public const string TwoFactorEnableRoute = "/2fa/enable";
    public const string TwoFactorVerifyRoute = "/2fa/verify";
    public const string TwoFactorDisableRoute = "/2fa/disable";
    public const string ChangePinRoute = "/change-pin";

    public static RouteGroupBuilder MapAuthenticatedEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost(LogoutRoute, HandleLogoutAsync);
        group.MapGet(MeRoute, HandleMeAsync);

        // US4 — password self-service (T134)
        group.MapPost(ChangePasswordRoute, HandleChangePasswordAsync);
        group.MapPost(ForgotPasswordRoute, HandleForgotPasswordAsync);
        group.MapPost(ResetPasswordRoute, HandleResetPasswordAsync);

        // US4 — sessions management (T134)
        group.MapGet(SessionsRoute, HandleListSessionsAsync);
        group.MapDelete("/sessions/{id:guid}", HandleRevokeSessionAsync);
        group.MapDelete(SessionsRoute, HandleRevokeAllSessionsAsync);

        // US4 — 2FA enrollment (T134)
        group.MapPost(TwoFactorEnableRoute, HandleTwoFactorEnableAsync);
        group.MapPost(TwoFactorVerifyRoute, HandleTwoFactorVerifyAsync);
        group.MapPost(TwoFactorDisableRoute, HandleTwoFactorDisableAsync);

        // Add-child redesign Phase 6.2: child self-service PIN change.
        group.MapPost(ChangePinRoute, HandleChangePinAsync);

        return group;
    }

    private static async Task<IResult> HandleChangePinAsync(
        ChangePinRequest? request,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IPasswordService passwords,
        IWeakPinBlocklist weakPinBlocklist,
        ICommandValidator<ChangePinCommand> validator,
        MuallimiDbContext db,
        IRateLimitService rateLimit,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var failReason))
            return AuthEndpointHelpers.FailEnvelope(401, failReason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (claims.Scope != "child")
            return AuthEndpointHelpers.FailEnvelope(403, "child_scope_required", "هذه العملية متاحة لحساب الطفل فقط.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var ip = AuthEndpointHelpers.ResolveIp(http);
        var rl = await rateLimit.IncrementAndCheckAsync("change-pin", claims.UserId.ToString("D"), 5, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!rl.Allowed)
            return AuthEndpointHelpers.FailEnvelope(429, "rate_limited", "تم تجاوز عدد المحاولات.", correlationId);

        var body = request ?? new ChangePinRequest();
        var cmd = new ChangePinCommand(
            ChildUserId: claims.UserId,
            CurrentPin: body.CurrentPin,
            NewPin: body.NewPin,
            IpAddress: ip,
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var child = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == claims.UserId, ct).ConfigureAwait(false);
        if (child is null
            || child.AccountType != Muallimi.Domain.Identity.Enums.AccountType.Managed
            || child.LoginMethod != "pin")
        {
            return AuthEndpointHelpers.FailEnvelope(403, "pin_not_applicable", "هذا الحساب لا يستخدم رمز PIN.", correlationId);
        }
        if (!passwords.VerifyWithDummyFallback(cmd.CurrentPin, child.PinHash))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "invalid_current_pin", "رمز PIN الحالي غير صحيح.", correlationId);
        }

        // Pull birth year from the StudentProfile so the new PIN cannot equal it.
        int? birthYear = null;
        var profile = await db.StudentProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == child.Id, ct).ConfigureAwait(false);
        if (profile?.Birthday is { } bd) birthYear = bd.Year;
        if (weakPinBlocklist.IsWeak(cmd.NewPin, birthYear))
        {
            return AuthEndpointHelpers.FailEnvelope(422, "pin_too_weak", "رمز PIN ضعيف. اختر رمزًا أصعب.", correlationId);
        }

        child.PinHash = passwords.Hash(cmd.NewPin);
        child.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return AuthEndpointHelpers.OkEnvelope(new { changed = true }, "تم تغيير رمز PIN.", correlationId);
    }

    private static async Task<IResult> HandleLogoutAsync(
        HttpContext http,
        IAuthService auth,
        ITokenService tokens,
        ISessionService sessions,
        ICommandValidator<LogoutCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var failReason))
        {
            return AuthEndpointHelpers.FailEnvelope(401, failReason ?? "unauthorized", "غير مصرّح.", correlationId);
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }

        var cmd = new LogoutCommand(
            SessionId: claims.SessionId,
            UserId: claims.UserId,
            RefreshToken: null,
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        }
        var outcome = await auth.LogoutAsync(cmd, ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { loggedOut = true }, outcome.Message, correlationId);
    }

    private static async Task<IResult> HandleMeAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        MuallimiDbContext db,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var failReason))
        {
            return AuthEndpointHelpers.FailEnvelope(401, failReason ?? "unauthorized", "غير مصرّح.", correlationId);
        }
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);
        }

        var user = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == claims.UserId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return AuthEndpointHelpers.FailEnvelope(404, "user_not_found", "المستخدم غير موجود.", correlationId);
        }
        var tenant = await db.IdentityTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == user.TenantId, ct).ConfigureAwait(false);
        var roles = await db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id && ur.RevokedAt == null)
            .Join(db.IdentityRoles.IgnoreQueryFilters(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var profile = new UserProfile
        {
            UserId = user.Id.ToString("D"),
            Email = user.Email,
            Username = user.Username,
            FullName = user.FullName,
            FullNameEn = user.FullNameEn,
            TenantId = user.TenantId.ToString("D"),
            TenantType = (tenant?.Type ?? Muallimi.Domain.Identity.Enums.TenantType.Family).ToString().ToLowerInvariant(),
            Roles = roles,
            Locale = user.Locale,
            AccountType = user.AccountType.ToString().ToLowerInvariant(),
            EmailVerified = user.EmailVerified,
            TwoFactorEnabled = user.TwoFactorEnabled,
            RequiresPasswordReset = user.RequiresPasswordReset,
            Status = user.Status.ToString().ToLowerInvariant(),
        };
        return AuthEndpointHelpers.OkEnvelope(profile, "ok", correlationId);
    }

    // ── US4: Password self-service ─────────────────────────────────────

    private static async Task<IResult> HandleChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        IPasswordResetService pwReset,
        ICommandValidator<ChangePasswordCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
            return AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var cmd = new ChangePasswordCommand(
            UserId: claims.UserId,
            CurrentPassword: request.CurrentPassword ?? string.Empty,
            NewPassword: request.NewPassword ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var result = await pwReset.ChangePasswordAsync(cmd, claims.SessionId, ct).ConfigureAwait(false);
        if (!result.Success)
            return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "change_failed", result.Message, correlationId, result.Errors);
        return AuthEndpointHelpers.OkEnvelope(new { passwordChanged = true }, result.Message, correlationId);
    }

    private static async Task<IResult> HandleForgotPasswordAsync(
        ForgotPasswordRequest request,
        HttpContext http,
        IPasswordResetService pwReset,
        ICommandValidator<ForgotPasswordCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new ForgotPasswordCommand(
            Email: request.Email ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await pwReset.ForgotPasswordAsync(cmd, ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { sent = true }, result.Message, correlationId);
    }

    private static async Task<IResult> HandleResetPasswordAsync(
        ResetPasswordRequest request,
        HttpContext http,
        IPasswordResetService pwReset,
        ICommandValidator<ResetPasswordCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        var cmd = new ResetPasswordCommand(
            Token: request.Token ?? string.Empty,
            NewPassword: request.NewPassword ?? string.Empty,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);
        var result = await pwReset.ResetPasswordAsync(cmd, ct).ConfigureAwait(false);
        if (!result.Success)
            return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "reset_failed", result.Message, correlationId, result.Errors);
        return AuthEndpointHelpers.OkEnvelope(new { passwordReset = true }, result.Message, correlationId);
    }

    // ── US4: Sessions management ───────────────────────────────────────

    private static async Task<IResult> HandleListSessionsAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
            return AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var list = await sessions.ListActiveSessionsAsync(claims.UserId, ct).ConfigureAwait(false);
        var data = list.Select(s => new
        {
            id = s.Id.ToString("D"),
            deviceName = s.DeviceName,
            ipAddress = s.IpAddress,
            userAgent = s.UserAgent,
            createdAt = s.CreatedAt,
            lastSeenAt = s.LastSeenAt,
            isCurrent = s.Id == claims.SessionId,
        }).ToList();
        return AuthEndpointHelpers.OkEnvelope(new { sessions = data }, "ok", correlationId);
    }

    private static async Task<IResult> HandleRevokeSessionAsync(
        Guid id,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
            return AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        // Only revoke sessions belonging to the calling user
        var list = await sessions.ListActiveSessionsAsync(claims.UserId, ct).ConfigureAwait(false);
        if (!list.Any(s => s.Id == id))
            return AuthEndpointHelpers.FailEnvelope(404, "session_not_found", "الجلسة غير موجودة.", correlationId);

        await sessions.RevokeAsync(id, ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { revoked = true }, "تم إنهاء الجلسة.", correlationId);
    }

    private static async Task<IResult> HandleRevokeAllSessionsAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
            return AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        await sessions.RevokeAllForUserAsync(claims.UserId, exceptSessionId: null, ct).ConfigureAwait(false);
        return AuthEndpointHelpers.OkEnvelope(new { allRevoked = true }, "تم إنهاء جميع الجلسات.", correlationId);
    }

    // ── US4: 2FA enrollment ────────────────────────────────────────────

    private static async Task<IResult> HandleTwoFactorEnableAsync(
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        ITwoFactorManagementService twoFactor,
        ICommandValidator<EnableTwoFactorCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
            return AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var cmd = new EnableTwoFactorCommand(claims.UserId, correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var result = await twoFactor.StartEnrollmentAsync(cmd, ct).ConfigureAwait(false);
        if (!result.Success)
            return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "enable_failed", result.Message, correlationId, result.Errors);
        return AuthEndpointHelpers.OkEnvelope(
            new { qrUri = result.QrUri, tempSecret = result.TempSecret },
            result.Message, correlationId);
    }

    private static async Task<IResult> HandleTwoFactorVerifyAsync(
        TwoFactorVerifyRequest request,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        ITwoFactorManagementService twoFactor,
        ICommandValidator<VerifyTwoFactorCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
            return AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var cmd = new VerifyTwoFactorCommand(claims.UserId, request.Code ?? string.Empty, correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var result = await twoFactor.VerifyEnrollmentAsync(cmd, ct).ConfigureAwait(false);
        if (!result.Success)
            return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "verify_failed", result.Message, correlationId, result.Errors);
        return AuthEndpointHelpers.OkEnvelope(
            new { twoFactorEnabled = true, recoveryCodes = result.RecoveryCodes },
            result.Message, correlationId);
    }

    private static async Task<IResult> HandleTwoFactorDisableAsync(
        TwoFactorDisableRequest request,
        HttpContext http,
        ITokenService tokens,
        ISessionService sessions,
        ITwoFactorManagementService twoFactor,
        IPasswordResetService pwReset,
        ICommandValidator<DisableTwoFactorCommand> validator,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var reason))
            return AuthEndpointHelpers.FailEnvelope(401, reason ?? "unauthorized", "غير مصرّح.", correlationId);
        if (!await sessions.IsSessionActiveAsync(claims.SessionId, ct).ConfigureAwait(false))
            return AuthEndpointHelpers.FailEnvelope(401, "session_revoked", "الجلسة منتهية.", correlationId);

        var cmd = new DisableTwoFactorCommand(
            UserId: claims.UserId,
            CurrentPassword: request.CurrentPassword ?? string.Empty,
            CorrelationId: correlationId);
        var errors = validator.Validate(cmd);
        if (errors.Count > 0)
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "فشل التحقق من المدخلات.", correlationId, errors);

        var result = await twoFactor.DisableAsync(cmd, ct).ConfigureAwait(false);
        if (!result.Success)
            return AuthEndpointHelpers.FailEnvelope(result.HttpStatus, result.ErrorCode ?? "disable_failed", result.Message, correlationId, result.Errors);
        return AuthEndpointHelpers.OkEnvelope(new { twoFactorDisabled = true }, result.Message, correlationId);
    }
}

// ── US4 request bodies ──────────────────────────────────────────────────

public sealed class ForgotPasswordRequest
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

public sealed class ResetPasswordRequest
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("newPassword")]
    public string? NewPassword { get; set; }
}

public sealed class TwoFactorVerifyRequest
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public sealed class TwoFactorDisableRequest
{
    [JsonPropertyName("currentPassword")]
    public string? CurrentPassword { get; set; }
}

/// <summary>
/// Extracts <c>sub</c> / <c>tenant_id</c> / <c>session_id</c> from the
/// <c>Authorization: Bearer …</c> header via
/// <see cref="ITokenService.ValidateAccessToken"/>. Shared by every
/// endpoint that needs the caller's identity without depending on the
/// JWT-bearer middleware (which we don't wire into the main-backend
/// pipeline — consumer-side middleware in <c>ai-service</c> /
/// <c>document-ingestion</c> does that for those repos).
/// </summary>
internal static class AuthClaimsReader
{
    public static bool TryExtract(
        HttpContext http,
        ITokenService tokens,
        out AuthClaims claims,
        out string? failReason)
    {
        claims = default!;
        failReason = null;

        var authHeader = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            failReason = "missing_bearer";
            return false;
        }
        var token = authHeader["Bearer ".Length..].Trim();
        var principal = tokens.ValidateAccessToken(token);
        if (principal is null)
        {
            failReason = "invalid_token";
            return false;
        }
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var tenant = principal.FindFirst("tenant_id")?.Value;
        var session = principal.FindFirst("session_id")?.Value;
        if (!Guid.TryParse(sub, out var userId)
            || !Guid.TryParse(tenant, out var tenantId)
            || !Guid.TryParse(session, out var sessionId))
        {
            failReason = "invalid_token_claims";
            return false;
        }
        // .NET's JwtSecurityTokenHandler has MapInboundClaims=true by default,
        // which remaps the JWT "roles" claim to ClaimTypes.Role via its
        // DefaultInboundClaimTypeMap. Check both — the short name (when
        // mapping is disabled) and the mapped URI (when it isn't).
        var roles = principal.FindAll("roles").Select(c => c.Value)
            .Concat(principal.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scope = principal.FindFirst("scope")?.Value;
        Guid? derivedFromSessionId = null;
        var derivedRaw = principal.FindFirst("derived_from_session_id")?.Value;
        if (!string.IsNullOrEmpty(derivedRaw) && Guid.TryParse(derivedRaw, out var derivedParsed))
        {
            derivedFromSessionId = derivedParsed;
        }
        claims = new AuthClaims(userId, tenantId, sessionId, roles, scope, derivedFromSessionId);
        http.User = principal;
        return true;
    }
}

internal readonly record struct AuthClaims(
    Guid UserId,
    Guid TenantId,
    Guid SessionId,
    string[] Roles,
    string? Scope,
    Guid? DerivedFromSessionId);
