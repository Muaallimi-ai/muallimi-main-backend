using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Services;
using Muallimi.Application.Audit;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Endpoints;

/// <summary>
/// Add-child redesign Phase 4 — parent profile-switch endpoints.
///
///   • POST /api/parent/switch-to-child — five-condition positive check
///     (parent role, Family tenant, Personal account, target Managed,
///     ManagedByUserId match). On success mints a 30-min child JWT with
///     scope=child + derived_from_session_id. NO refresh token; sessions
///     are re-derived from the parent on demand.
///   • POST /api/parent/exit-child-session — verifies parent password,
///     revokes the child session, mints a fresh parent token. Forgot-
///     password fallback triggers email reset without ending the child
///     session.
/// </summary>
public static class ParentSwitchEndpoints
{
    public const string GroupRoute = "/parent";
    public const string SwitchToChildRoute = "/switch-to-child";
    public const string ExitChildSessionRoute = "/exit-child-session";

    /// <summary>30-minute access-token TTL for profile-switch sessions (decision #2).</summary>
    public static readonly TimeSpan ProfileSwitchTtl = TimeSpan.FromMinutes(30);

    public static RouteGroupBuilder MapParentSwitchEndpoints(this RouteGroupBuilder identityGroup)
    {
        identityGroup.MapPost(GroupRoute + SwitchToChildRoute, HandleSwitchToChildAsync);
        identityGroup.MapPost(GroupRoute + ExitChildSessionRoute, HandleExitChildSessionAsync);
        return identityGroup;
    }

    public sealed class SwitchToChildRequest
    {
        [JsonPropertyName("childId")]
        public Guid ChildId { get; set; }
    }

    public sealed class ExitChildSessionRequest
    {
        [JsonPropertyName("parentPassword")]
        public string? ParentPassword { get; set; }

        [JsonPropertyName("requestPasswordReset")]
        public bool RequestPasswordReset { get; set; }
    }

    /// <summary>
    /// Response payload for /switch-to-child. Pinned to camelCase via
    /// explicit JsonPropertyName so the global snake_case JSON policy
    /// doesn't break the frontend (which reads accessToken / expiresAt /
    /// childId / scope as camelCase).
    /// </summary>
    public sealed class SwitchToChildResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expiresAt")]
        public DateTime ExpiresAt { get; set; }

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = "child";

        [JsonPropertyName("childId")]
        public string ChildId { get; set; } = string.Empty;
    }

    /// <summary>Response payload for /exit-child-session — same camelCase pinning.</summary>
    public sealed class ExitChildSessionResponse
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTime? ExpiresAt { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("resetEmailSent")]
        public bool? ResetEmailSent { get; set; }
    }

    private static async Task<IResult> HandleSwitchToChildAsync(
        SwitchToChildRequest request,
        HttpContext http,
        MuallimiDbContext db,
        ITokenService tokens,
        ISessionService sessions,
        IProfileIdsResolver profileIds,
        IRateLimitService rateLimit,
        ISubscriptionGuard subscriptionGuard,
        AuditEventEmitter audit,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var failReason))
        {
            return AuthEndpointHelpers.FailEnvelope(401, failReason ?? "unauthorized", "غير مصرّح.", correlationId);
        }
        if (claims.Scope == "child")
        {
            return AuthEndpointHelpers.FailEnvelope(403, "child_scope_blocked", "هذه العملية متاحة لأولياء الأمور فقط.", correlationId);
        }

        var rl = await rateLimit.IncrementAndCheckAsync("switch-to-child", claims.UserId.ToString("D"), 10, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!rl.Allowed)
        {
            return AuthEndpointHelpers.FailEnvelope(429, "rate_limited", "تم تجاوز عدد المحاولات.", correlationId);
        }

        if (request.ChildId == Guid.Empty)
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "معرّف الطفل مطلوب.", correlationId);
        }

        // Five-condition positive check (security non-negotiable #4).
        if (!claims.Roles.Any(r => string.Equals(r, "parent", StringComparison.Ordinal)))
        {
            return AuthEndpointHelpers.FailEnvelope(403, "child_scope_blocked", "غير مصرّح.", correlationId);
        }

        var parent = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == claims.UserId, ct).ConfigureAwait(false);
        if (parent is null || parent.AccountType != AccountType.Personal)
        {
            return AuthEndpointHelpers.FailEnvelope(403, "child_scope_blocked", "غير مصرّح.", correlationId);
        }

        var parentTenant = await db.IdentityTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == parent.TenantId, ct).ConfigureAwait(false);
        if (parentTenant is null || parentTenant.Type != TenantType.Family)
        {
            return AuthEndpointHelpers.FailEnvelope(403, "child_scope_blocked", "هذه العملية متاحة لحسابات العائلة فقط.", correlationId);
        }

        var child = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == request.ChildId, ct).ConfigureAwait(false);
        if (child is null
            || child.AccountType != AccountType.Managed
            || child.ManagedByUserId != parent.Id
            || child.TenantId != parent.TenantId)
        {
            // Same opaque error for all five-condition failures so the
            // caller cannot probe the existence of children outside the
            // family.
            return AuthEndpointHelpers.FailEnvelope(403, "child_scope_blocked", "غير مصرّح.", correlationId);
        }

        if (child.Status != UserStatus.Active)
        {
            return AuthEndpointHelpers.FailEnvelope(403, "child_inactive", "حساب الطفل غير مفعّل.", correlationId);
        }

        // Phase 7.3: block-on-login subscription gate.
        var subGate = await subscriptionGuard.CheckActiveAsync(parent.TenantId, ct).ConfigureAwait(false);
        if (!subGate.Allowed)
        {
            return AuthEndpointHelpers.FailEnvelope(402, "subscription_expired", "الاشتراك منتهٍ. يرجى تجديد الاشتراك للمتابعة.", correlationId);
        }

        // Mint a derived child session.
        var session = await sessions.CreateAsync(new CreateSessionInput(
            UserId: child.Id,
            IpAddress: AuthEndpointHelpers.ResolveIp(http),
            UserAgent: AuthEndpointHelpers.ResolveUserAgent(http),
            DeviceName: null,
            DeviceType: DeviceType.Unknown,
            DerivedFromSessionId: claims.SessionId), ct).ConfigureAwait(false);

        var roles = await db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(r => r.UserId == child.Id && r.RevokedAt == null)
            .Join(db.IdentityRoles.IgnoreQueryFilters(), ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var resolvedProfileIds = await profileIds.ResolveAsync(child.Id, child.TenantId, ct).ConfigureAwait(false);

        // Pull the child's emoji + bg colour from their StudentProfile
        // so the topbar after the switch shows the actual avatar instead
        // of the parent's letter initial.
        var childProfile = await db.StudentProfiles.IgnoreQueryFilters()
            .Where(p => p.UserId == child.Id)
            .Select(p => new { p.AvatarReference, p.AvatarBgColor })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var access = tokens.GenerateAccessToken(
            child,
            parentTenant.Type,
            roles,
            session.Id,
            impersonation: null,
            profileIds: resolvedProfileIds,
            derivedFromSessionId: claims.SessionId,
            overrideLifetime: ProfileSwitchTtl,
            avatarEmoji: childProfile?.AvatarReference,
            avatarBgColor: childProfile?.AvatarBgColor);

        audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Login.ToString(),
            ActorId = parent.Id.ToString("D"),
            TenantId = parent.TenantId.ToString("D"),
            Action = "parent_session_switched_to_child",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = correlationId,
        });

        return AuthEndpointHelpers.OkEnvelope(new SwitchToChildResponse
        {
            AccessToken = access.Token,
            ExpiresAt = access.ExpiresAt,
            Scope = "child",
            ChildId = child.Id.ToString("D"),
        }, "تم تبديل الجلسة.", correlationId);
    }

    private static async Task<IResult> HandleExitChildSessionAsync(
        ExitChildSessionRequest request,
        HttpContext http,
        MuallimiDbContext db,
        ITokenService tokens,
        ISessionService sessions,
        IPasswordService passwords,
        IProfileIdsResolver profileIds,
        IRateLimitService rateLimit,
        IPasswordResetService passwordReset,
        AuditEventEmitter audit,
        CancellationToken ct)
    {
        var correlationId = AuthEndpointHelpers.ResolveCorrelationId(http);
        if (!AuthClaimsReader.TryExtract(http, tokens, out var claims, out var failReason))
        {
            return AuthEndpointHelpers.FailEnvelope(401, failReason ?? "unauthorized", "غير مصرّح.", correlationId);
        }
        if (claims.Scope != "child" || claims.DerivedFromSessionId is not { } parentSessionId)
        {
            return AuthEndpointHelpers.FailEnvelope(403, "not_in_child_session", "هذه العملية متاحة من جلسة طفل فقط.", correlationId);
        }

        var ip = AuthEndpointHelpers.ResolveIp(http);
        var rl = await rateLimit.IncrementAndCheckAsync("exit-child-session", ip, 5, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!rl.Allowed)
        {
            return AuthEndpointHelpers.FailEnvelope(429, "rate_limited", "تم تجاوز عدد المحاولات.", correlationId);
        }

        // Resolve the parent via the derived session pointer.
        var parentSession = await db.IdentityUserSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == parentSessionId, ct).ConfigureAwait(false);
        if (parentSession is null)
        {
            return AuthEndpointHelpers.FailEnvelope(401, "parent_session_missing", "الجلسة الأصلية غير موجودة.", correlationId);
        }
        var parent = await db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == parentSession.UserId, ct).ConfigureAwait(false);
        if (parent is null)
        {
            return AuthEndpointHelpers.FailEnvelope(401, "parent_missing", "ولي الأمر غير موجود.", correlationId);
        }

        if (request.RequestPasswordReset)
        {
            await passwordReset.ForgotPasswordAsync(new ForgotPasswordCommand(
                Email: parent.Email,
                IpAddress: ip,
                CorrelationId: correlationId), ct).ConfigureAwait(false);
            audit.Emit(new AuditEvent
            {
                EventCategory = AuthEventCategory.PasswordReset.ToString(),
                ActorId = claims.UserId.ToString("D"),
                TenantId = claims.TenantId.ToString("D"),
                Action = "child_session_switch_back_password_reset_requested",
                TargetType = "User",
                TargetId = parent.Id.ToString("D"),
                Outcome = "succeeded",
                CorrelationId = correlationId,
            });
            return AuthEndpointHelpers.OkEnvelope(
                new ExitChildSessionResponse { ResetEmailSent = true },
                "تم إرسال رابط إعادة التعيين.",
                correlationId);
        }

        if (string.IsNullOrEmpty(request.ParentPassword))
        {
            return AuthEndpointHelpers.FailEnvelope(422, "validation_failed", "كلمة مرور ولي الأمر مطلوبة.", correlationId);
        }

        var ok = passwords.VerifyWithDummyFallback(request.ParentPassword, parent.PasswordHash);
        if (!ok)
        {
            audit.Emit(new AuditEvent
            {
                EventCategory = AuthEventCategory.Login.ToString(),
                ActorId = claims.UserId.ToString("D"),
                TenantId = claims.TenantId.ToString("D"),
                Action = "child_session_switch_back_failed",
                TargetType = "User",
                TargetId = parent.Id.ToString("D"),
                Outcome = "failed",
                CorrelationId = correlationId,
            });
            return AuthEndpointHelpers.FailEnvelope(401, "invalid_parent_password", "كلمة مرور ولي الأمر غير صحيحة.", correlationId);
        }

        // Revoke the child session.
        await sessions.RevokeAsync(claims.SessionId, ct).ConfigureAwait(false);

        // Verify the parent session is still active before re-issuing.
        if (!await sessions.IsSessionActiveAsync(parentSession.Id, ct).ConfigureAwait(false))
        {
            return AuthEndpointHelpers.FailEnvelope(401, "parent_session_revoked", "الجلسة الأصلية منتهية. سجّل الدخول من جديد.", correlationId);
        }

        var parentTenant = await db.IdentityTenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == parent.TenantId, ct).ConfigureAwait(false);
        var roles = await db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(r => r.UserId == parent.Id && r.RevokedAt == null)
            .Join(db.IdentityRoles.IgnoreQueryFilters(), ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var resolvedProfileIds = await profileIds.ResolveAsync(parent.Id, parent.TenantId, ct).ConfigureAwait(false);

        var access = tokens.GenerateAccessToken(
            parent,
            parentTenant.Type,
            roles,
            parentSession.Id,
            impersonation: null,
            profileIds: resolvedProfileIds);

        audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Login.ToString(),
            ActorId = parent.Id.ToString("D"),
            TenantId = parent.TenantId.ToString("D"),
            Action = "child_session_switched_back",
            TargetType = "User",
            TargetId = claims.UserId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = correlationId,
        });

        return AuthEndpointHelpers.OkEnvelope(
            new ExitChildSessionResponse
            {
                AccessToken = access.Token,
                ExpiresAt = access.ExpiresAt,
                Scope = "parent",
            },
            "تم العودة إلى حساب ولي الأمر.",
            correlationId);
    }
}
