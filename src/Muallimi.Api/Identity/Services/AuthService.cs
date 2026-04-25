using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Audit;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T071 — Core auth pipeline. Orchestrates
/// <see cref="IPasswordService"/>, <see cref="ITokenService"/>,
/// <see cref="IRateLimitService"/>, <see cref="ISessionService"/>,
/// <see cref="AuditEventEmitter"/>, and
/// <see cref="IIdentityNotificationSender"/> across the five US1
/// verbs: register-parent, register-school-admin, login, refresh,
/// logout.
///
/// This file deliberately talks to <see cref="MuallimiDbContext"/>
/// directly — every other service in the repo does the same, and
/// introducing a repository layer for Phase 9 alone would drift from
/// the house style.
/// </summary>
public interface IAuthService
{
    Task<AuthOutcome> RegisterParentAsync(RegisterParentCommand cmd, CancellationToken ct = default);
    Task<AuthOutcome> RegisterSchoolAdminAsync(RegisterSchoolAdminCommand cmd, CancellationToken ct = default);
    Task<AuthOutcome> LoginAsync(LoginCommand cmd, CancellationToken ct = default);
    Task<AuthOutcome> RefreshAsync(RefreshTokenCommand cmd, CancellationToken ct = default);
    Task<AuthOutcome> LogoutAsync(LogoutCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Runs the post-password-verification login pipeline for an
    /// already-looked-up user: status checks, session creation, JWT +
    /// refresh minting, audit event. Used by the child-PIN login path
    /// (phone + PIN) which resolves the user via a different lookup
    /// than <see cref="LoginAsync"/>'s identifier-based one. Callers
    /// MUST have already verified the password.
    /// </summary>
    Task<AuthOutcome> CompleteLoginAsync(
        User user,
        string ipAddress,
        string? userAgent,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record AuthOutcome(
    bool Success,
    int HttpStatus,
    string Message,
    AuthResponse? Payload = null,
    TwoFactorChallengeResponse? TwoFactor = null,
    IReadOnlyList<ApiResponseError>? Errors = null,
    string? ErrorCode = null)
{
    public static AuthOutcome Ok(AuthResponse payload, string message)
        => new(true, 200, message, Payload: payload);

    public static AuthOutcome Created(AuthResponse payload, string message)
        => new(true, 201, message, Payload: payload);

    public static AuthOutcome TwoFactorRequired(TwoFactorChallengeResponse challenge, string message)
        => new(false, 401, message, TwoFactor: challenge, ErrorCode: "two_factor_required");

    public static AuthOutcome Fail(int status, string code, string message, IReadOnlyList<ApiResponseError>? errors = null)
        => new(false, status, message, Errors: errors ?? new[] { new ApiResponseError { Code = code, Message = message } }, ErrorCode: code);
}

public sealed class AuthService : IAuthService
{
    public const int LockoutThreshold = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly ITokenService _tokens;
    private readonly IRateLimitService _rateLimit;
    private readonly ISessionService _sessions;
    private readonly AuditEventEmitter _audit;
    private readonly IIdentityNotificationSender _notifications;
    private readonly IEmailVerificationService _verification;
    private readonly IVerificationLinkBuilder _linkBuilder;
    private readonly ITwoFactorManagementService? _twoFactor;
    private readonly IUnusualLoginDetector? _unusualLoginDetector;
    private readonly IProfileIdsResolver _profileIds;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        MuallimiDbContext db,
        IPasswordService passwords,
        ITokenService tokens,
        IRateLimitService rateLimit,
        ISessionService sessions,
        AuditEventEmitter audit,
        IIdentityNotificationSender notifications,
        IEmailVerificationService verification,
        IVerificationLinkBuilder linkBuilder,
        IProfileIdsResolver profileIds,
        ILogger<AuthService> logger,
        ITwoFactorManagementService? twoFactor = null,
        IUnusualLoginDetector? unusualLoginDetector = null)
    {
        _db = db;
        _passwords = passwords;
        _tokens = tokens;
        _rateLimit = rateLimit;
        _sessions = sessions;
        _audit = audit;
        _notifications = notifications;
        _verification = verification;
        _linkBuilder = linkBuilder;
        _profileIds = profileIds;
        _twoFactor = twoFactor;
        _unusualLoginDetector = unusualLoginDetector;
        _logger = logger;
    }

    // ── Register (parent) ──────────────────────────────────────────────

    public async Task<AuthOutcome> RegisterParentAsync(RegisterParentCommand cmd, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(cmd.Email);
        var rl = await _rateLimit.IncrementAndCheckAsync("register-ip", cmd.IpAddress, 3, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!rl.Allowed)
        {
            return AuthOutcome.Fail(429, "rate_limited", "تم تجاوز عدد محاولات التسجيل.");
        }

        var emailTaken = await _db.IdentityUsers.IgnoreQueryFilters()
            .AnyAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        if (emailTaken)
        {
            return AuthOutcome.Fail(409, "email_taken", "البريد الإلكتروني مستخدم بالفعل.");
        }

        var normalizedPhone = Muallimi.Application.Identity.Commands.ValidationRules.NormalizePhone(cmd.PhoneNumber);
        if (!string.IsNullOrEmpty(normalizedPhone))
        {
            var phoneTaken = await _db.IdentityUsers.IgnoreQueryFilters()
                .AnyAsync(u => u.PhoneNumber == normalizedPhone, ct).ConfigureAwait(false);
            if (phoneTaken)
            {
                return AuthOutcome.Fail(409, "phone_taken", "رقم الهاتف مستخدم مسبقًا.");
            }
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Type = TenantType.Family,
            DisplayName = cmd.FullName,
            Locale = cmd.Locale,
            Status = TenantStatus.Active,
            Metadata = "{}",
            CreatedAt = DateTime.UtcNow,
        };

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            AccountType = AccountType.Personal,
            Email = cmd.Email.Trim(),
            NormalizedEmail = normalized,
            FullName = cmd.FullName.Trim(),
            FullNameEn = cmd.FullNameEn?.Trim(),
            Locale = cmd.Locale,
            Status = UserStatus.PendingEmailVerification,
            PasswordHash = _passwords.Hash(cmd.Password),
            PasswordChangedAt = DateTime.UtcNow,
            PhoneNumber = normalizedPhone,
            CreatedAt = DateTime.UtcNow,
        };
        user.AssertAccountTypeInvariants();

        var parentRole = await FindRoleAsync("parent", ct).ConfigureAwait(false);
        if (parentRole is null)
        {
            return AuthOutcome.Fail(500, "role_missing", "الدور غير موجود.");
        }
        var grant = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = parentRole.Id,
            TenantId = tenant.Id,
            GrantedBy = user.Id,
            GrantedAt = DateTime.UtcNow,
        };

        // Parent profile is created atomically with the user + tenant — it
        // owns notification preferences, child links, and badge state, so
        // every authenticated parent must have one. The id is fresh; the
        // legacy IdentityId column is set to the same value (it predates the
        // Phase 9 UserId FK and is kept non-null for back-compat).
        var now = DateTime.UtcNow;
        var parentProfile = new ParentProfile
        {
            ParentProfileId = Guid.NewGuid(),
            TenantId = tenant.Id,
            IdentityId = user.Id,
            UserId = user.Id,
            PreferredLanguage = cmd.Locale == "en" ? "en" : "ar",
            Locale = cmd.Locale == "en" ? "en-US" : "ar-EG",
            Timezone = "Africa/Cairo",
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.IdentityTenants.Add(tenant);
        _db.IdentityUsers.Add(user);
        _db.IdentityUserRoles.Add(grant);
        _db.ParentProfiles.Add(parentProfile);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Register.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = tenant.Id.ToString("D"),
            Action = "register_parent",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        await IssueVerificationAndNotifyAsync(user, cmd.CorrelationId, ct).ConfigureAwait(false);

        // Registration returns a "created" outcome — the user cannot log in
        // until email is verified, so we do not issue access tokens here.
        var payload = BuildAuthResponsePlaceholder(user, tenant.Type, new[] { "parent" });
        return AuthOutcome.Created(payload, "تم إنشاء الحساب. يرجى تأكيد البريد الإلكتروني.");
    }

    public async Task<AuthOutcome> RegisterSchoolAdminAsync(RegisterSchoolAdminCommand cmd, CancellationToken ct = default)
    {
        var normalized = NormalizeEmail(cmd.Email);
        var rl = await _rateLimit.IncrementAndCheckAsync("register-ip", cmd.IpAddress, 3, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!rl.Allowed)
        {
            return AuthOutcome.Fail(429, "rate_limited", "تم تجاوز عدد محاولات التسجيل.");
        }
        var emailTaken = await _db.IdentityUsers.IgnoreQueryFilters()
            .AnyAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        if (emailTaken)
        {
            return AuthOutcome.Fail(409, "email_taken", "البريد الإلكتروني مستخدم بالفعل.");
        }

        var normalizedPhone = Muallimi.Application.Identity.Commands.ValidationRules.NormalizePhone(cmd.PhoneNumber);
        if (!string.IsNullOrEmpty(normalizedPhone))
        {
            var phoneTaken = await _db.IdentityUsers.IgnoreQueryFilters()
                .AnyAsync(u => u.PhoneNumber == normalizedPhone, ct).ConfigureAwait(false);
            if (phoneTaken)
            {
                return AuthOutcome.Fail(409, "phone_taken", "رقم الهاتف مستخدم مسبقًا.");
            }
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Type = TenantType.School,
            DisplayName = cmd.SchoolDisplayName,
            Locale = cmd.Locale,
            Status = TenantStatus.Active,
            Metadata = "{}",
            CreatedAt = DateTime.UtcNow,
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            AccountType = AccountType.Personal,
            Email = cmd.Email.Trim(),
            NormalizedEmail = normalized,
            FullName = cmd.FullName.Trim(),
            FullNameEn = cmd.FullNameEn?.Trim(),
            Locale = cmd.Locale,
            Status = UserStatus.PendingEmailVerification,
            PasswordHash = _passwords.Hash(cmd.Password),
            PasswordChangedAt = DateTime.UtcNow,
            PhoneNumber = normalizedPhone,
            CreatedAt = DateTime.UtcNow,
        };
        user.AssertAccountTypeInvariants();

        var schoolAdminRole = await FindRoleAsync("school-admin", ct).ConfigureAwait(false);
        if (schoolAdminRole is null)
        {
            return AuthOutcome.Fail(500, "role_missing", "الدور غير موجود.");
        }
        var grant = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = schoolAdminRole.Id,
            TenantId = tenant.Id,
            GrantedBy = user.Id,
            GrantedAt = DateTime.UtcNow,
        };

        _db.IdentityTenants.Add(tenant);
        _db.IdentityUsers.Add(user);
        _db.IdentityUserRoles.Add(grant);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Register.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = tenant.Id.ToString("D"),
            Action = "register_school_admin",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        await IssueVerificationAndNotifyAsync(user, cmd.CorrelationId, ct).ConfigureAwait(false);

        var payload = BuildAuthResponsePlaceholder(user, tenant.Type, new[] { "school-admin" });
        return AuthOutcome.Created(payload, "تم إنشاء الحساب. يرجى تأكيد البريد الإلكتروني.");
    }

    // ── Login ──────────────────────────────────────────────────────────

    public async Task<AuthOutcome> LoginAsync(LoginCommand cmd, CancellationToken ct = default)
    {
        var ipRl = await _rateLimit.IncrementAndCheckAsync("login-ip", cmd.IpAddress, 5, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!ipRl.Allowed)
        {
            return AuthOutcome.Fail(429, "rate_limited", "تم تجاوز عدد محاولات الدخول.");
        }

        var identifier = cmd.Identifier.Trim();
        var user = await LookupForLoginAsync(identifier, ct).ConfigureAwait(false);

        // Timing-invariant password verification (runs BCrypt even when user is null).
        var verified = _passwords.VerifyWithDummyFallback(cmd.Password, user?.PasswordHash);

        if (user is null || !verified)
        {
            if (user is not null)
            {
                user.RegisterFailedLogin(LockoutThreshold, LockoutDuration);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                var action = user.Status == UserStatus.Locked ? "login_locked" : "login_failed";
                _audit.Emit(new AuditEvent
                {
                    EventCategory = AuthEventCategory.Login.ToString(),
                    ActorId = user.Id.ToString("D"),
                    TenantId = user.TenantId.ToString("D"),
                    Action = action,
                    TargetType = "User",
                    TargetId = user.Id.ToString("D"),
                    Outcome = "failed",
                    CorrelationId = cmd.CorrelationId,
                });
            }
            return AuthOutcome.Fail(401, "invalid_credentials", "البريد الإلكتروني أو كلمة المرور غير صحيحة.");
        }

        if (user.Status == UserStatus.Locked && user.LockoutEnd is { } end && end > DateTime.UtcNow)
        {
            return AuthOutcome.Fail(423, "account_locked", "الحساب مقفل مؤقتًا. حاول مجددًا لاحقًا.");
        }
        if (user.Status == UserStatus.Suspended)
        {
            return AuthOutcome.Fail(403, "account_suspended", "الحساب معلّق.");
        }
        if (user.Status == UserStatus.PendingEmailVerification)
        {
            return AuthOutcome.Fail(403, "email_not_verified", "يرجى تأكيد البريد الإلكتروني أولًا.");
        }
        if (user.Status == UserStatus.Archived)
        {
            return AuthOutcome.Fail(401, "invalid_credentials", "البريد الإلكتروني أو كلمة المرور غير صحيحة.");
        }
        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(cmd.TwoFactorCode))
            {
                var challenge = new TwoFactorChallengeResponse
                {
                    TwoFactorRequired = true,
                    TempToken = Guid.NewGuid().ToString("N"),
                };
                return AuthOutcome.TwoFactorRequired(challenge, "يتطلب التحقق بخطوتين");
            }
            // Verify TOTP code (US4 T131)
            if (_twoFactor is not null)
            {
                var totpValid = await _twoFactor.VerifyTotpCodeAsync(user.Id, cmd.TwoFactorCode, ct)
                    .ConfigureAwait(false);
                if (!totpValid)
                {
                    return AuthOutcome.Fail(401, "invalid_totp", "رمز التحقق بخطوتين غير صحيح.");
                }
            }
        }

        return await CompleteLoginAsync(user, cmd.IpAddress, cmd.UserAgent, cmd.CorrelationId, ct)
            .ConfigureAwait(false);
    }

    // ── Shared post-auth login completion ──────────────────────────────

    /// <summary>
    /// Runs session+token minting, audit, and unusual-login detection
    /// for an already-authenticated user. Called by <see cref="LoginAsync"/>
    /// after password verification, and by the child-PIN endpoint after
    /// PIN verification. Does NOT perform status/2FA/lockout guards —
    /// the caller is expected to have checked those.
    /// </summary>
    public async Task<AuthOutcome> CompleteLoginAsync(
        User user,
        string ipAddress,
        string? userAgent,
        string correlationId,
        CancellationToken ct = default)
    {
        // Credentials OK + user admissible — mark a successful login.
        user.MarkSuccessfulLogin(ipAddress);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _rateLimit.ClearLockoutAsync(user.Id.ToString("D"), ct).ConfigureAwait(false);

        var tenant = await _db.IdentityTenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == user.TenantId, ct).ConfigureAwait(false);
        var roleNames = await ResolveActiveRolesAsync(user.Id, ct).ConfigureAwait(false);

        var session = await _sessions.CreateAsync(new CreateSessionInput(
            UserId: user.Id,
            IpAddress: ipAddress,
            UserAgent: userAgent,
            DeviceName: InferDeviceName(userAgent),
            DeviceType: DeviceType.Unknown), ct).ConfigureAwait(false);

        var profileIds = await _profileIds.ResolveAsync(user.Id, user.TenantId, ct).ConfigureAwait(false);
        var access = _tokens.GenerateAccessToken(user, tenant.Type, roleNames, session.Id, profileIds: profileIds);
        var refresh = _tokens.GenerateRefreshToken();
        _db.IdentityRefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            SessionId = session.Id,
            TokenHash = refresh.hash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenLifetime),
            CreatedByIp = ipAddress,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Login.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "login_success",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = correlationId,
        });

        // T148 — Unusual-login detection and notification.
        if (_unusualLoginDetector is not null)
        {
            try
            {
                var isUnusual = await _unusualLoginDetector.RecordAndDetectAsync(
                    user.Id, ipAddress, userAgent, ct).ConfigureAwait(false);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (isUnusual)
                {
                    var deviceSummary = InferDeviceName(userAgent) ?? userAgent ?? "unknown";
                    var location = ipAddress;

                    if (user.AccountType == AccountType.Personal)
                    {
                        // Notify the user themselves.
                        _ = _notifications.SendUnusualLoginAsync(
                            new IdentityNotificationRecipient(
                                user.TenantId, user.Id, user.Email, user.FullName, user.Locale),
                            deviceSummary, location, correlationId, ct);
                    }
                    else if (user.AccountType == AccountType.Managed && user.ManagedByUserId.HasValue)
                    {
                        // Notify the managing parent.
                        var parent = await _db.IdentityUsers.IgnoreQueryFilters()
                            .FirstOrDefaultAsync(p => p.Id == user.ManagedByUserId.Value, ct)
                            .ConfigureAwait(false);
                        if (parent is not null)
                        {
                            _ = _notifications.SendChildUnusualLoginAsync(
                                new IdentityNotificationRecipient(
                                    parent.TenantId, parent.Id, parent.Email, parent.FullName, parent.Locale),
                                user.FullName, deviceSummary, location, correlationId, ct);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unusual-login detection failed for user {UserId}", user.Id);
            }
        }

        return AuthOutcome.Ok(
            BuildAuthResponse(user, tenant.Type, roleNames, access.Token, refresh.token, access.ExpiresAt),
            "تم تسجيل الدخول بنجاح");
    }

    // ── Refresh ────────────────────────────────────────────────────────

    public async Task<AuthOutcome> RefreshAsync(RefreshTokenCommand cmd, CancellationToken ct = default)
    {
        var rl = await _rateLimit.IncrementAndCheckAsync("refresh-ip", cmd.IpAddress, 10, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (!rl.Allowed)
        {
            return AuthOutcome.Fail(429, "rate_limited", "تم تجاوز عدد محاولات التحديث.");
        }

        var hash = JwtTokenServiceExtensions.HashRefreshToken(cmd.RefreshToken);
        var token = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct).ConfigureAwait(false);
        if (token is null)
        {
            return AuthOutcome.Fail(401, "invalid_refresh_token", "رمز التحديث غير صالح.");
        }

        if (!token.IsActive)
        {
            // Reuse detection — revoke the entire family and the session.
            await RevokeFamilyAsync(token.SessionId, "reused", ct).ConfigureAwait(false);
            await _sessions.RevokeAsync(token.SessionId, ct).ConfigureAwait(false);
            var reuseUser = await _db.IdentityUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == token.UserId, ct).ConfigureAwait(false);
            _audit.Emit(new AuditEvent
            {
                EventCategory = AuthEventCategory.Login.ToString(),
                ActorId = token.UserId.ToString("D"),
                TenantId = (reuseUser?.TenantId ?? Guid.Empty).ToString("D"),
                Action = "refresh_reuse_detected",
                TargetType = "RefreshToken",
                TargetId = token.Id.ToString("D"),
                Outcome = "blocked",
                CorrelationId = cmd.CorrelationId,
            });
            return AuthOutcome.Fail(401, "refresh_token_reused", "تم اكتشاف استخدام مكرر — تم إنهاء الجلسة.");
        }

        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == token.UserId, ct).ConfigureAwait(false);
        if (user is null || user.Status == UserStatus.Archived)
        {
            return AuthOutcome.Fail(401, "invalid_refresh_token", "رمز التحديث غير صالح.");
        }
        if (user.Status == UserStatus.Suspended)
        {
            return AuthOutcome.Fail(403, "account_suspended", "الحساب معلّق.");
        }

        var tenant = await _db.IdentityTenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == user.TenantId, ct).ConfigureAwait(false);
        var roles = await ResolveActiveRolesAsync(user.Id, ct).ConfigureAwait(false);

        var newRefresh = _tokens.GenerateRefreshToken();
        var newTokenId = Guid.NewGuid();
        _db.IdentityRefreshTokens.Add(new RefreshToken
        {
            Id = newTokenId,
            UserId = user.Id,
            SessionId = token.SessionId,
            TokenHash = newRefresh.hash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(RefreshTokenLifetime),
            CreatedByIp = cmd.IpAddress,
        });
        token.MarkRotated(newTokenId);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var profileIds = await _profileIds.ResolveAsync(user.Id, user.TenantId, ct).ConfigureAwait(false);
        var access = _tokens.GenerateAccessToken(user, tenant.Type, roles, token.SessionId, profileIds: profileIds);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Login.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "refresh",
            TargetType = "RefreshToken",
            TargetId = newTokenId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return AuthOutcome.Ok(
            BuildAuthResponse(user, tenant.Type, roles, access.Token, newRefresh.token, access.ExpiresAt),
            "تم تحديث الجلسة");
    }

    // ── Logout ─────────────────────────────────────────────────────────

    public async Task<AuthOutcome> LogoutAsync(LogoutCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct).ConfigureAwait(false);

        var tokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.SessionId == cmd.SessionId && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in tokens)
        {
            t.MarkLoggedOut();
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _sessions.RevokeAsync(cmd.SessionId, ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Logout.ToString(),
            ActorId = cmd.UserId.ToString("D"),
            TenantId = (user?.TenantId ?? Guid.Empty).ToString("D"),
            Action = "logout",
            TargetType = "UserSession",
            TargetId = cmd.SessionId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return new AuthOutcome(true, 200, "تم تسجيل الخروج");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    internal static string NormalizeEmail(string email)
        => (email ?? string.Empty).Trim().ToLowerInvariant();

    private Task<Role?> FindRoleAsync(string roleName, CancellationToken ct)
        => _db.IdentityRoles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Name == roleName, ct);

    private async Task<User?> LookupForLoginAsync(string identifier, CancellationToken ct)
    {
        if (identifier.Contains('@'))
        {
            var normalized = NormalizeEmail(identifier);
            return await _db.IdentityUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        }
        // Phone-as-identifier: accept Egyptian mobile (+20/0 prefix or
        // bare 10-digit core) and normalize to the 10-digit canonical form.
        var normalizedPhone = Muallimi.Application.Identity.Commands.ValidationRules.NormalizePhone(identifier);
        if (!string.IsNullOrEmpty(normalizedPhone))
        {
            return await _db.IdentityUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.PhoneNumber == normalizedPhone, ct).ConfigureAwait(false);
        }
        var normalizedUsername = identifier.ToLowerInvariant();
        return await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> ResolveActiveRolesAsync(Guid userId, CancellationToken ct)
    {
        var pairs = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .Join(_db.IdentityRoles.IgnoreQueryFilters(), ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        return pairs;
    }

    private async Task RevokeFamilyAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        var all = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.SessionId == sessionId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in all)
        {
            if (t.RevokedAt is null)
            {
                t.MarkFamilyRevoked();
            }
            else if (reason == "reused")
            {
                t.MarkCompromised();
            }
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private AuthResponse BuildAuthResponsePlaceholder(User user, TenantType tenantType, IReadOnlyList<string> roles)
        => new()
        {
            AccessToken = string.Empty,
            RefreshToken = string.Empty,
            ExpiresIn = 0,
            UserId = user.Id.ToString("D"),
            Email = user.Email,
            FullName = user.FullName,
            FullNameEn = user.FullNameEn,
            TenantId = user.TenantId.ToString("D"),
            TenantType = tenantType.ToString().ToLowerInvariant(),
            Roles = roles,
            Locale = user.Locale,
            EmailVerified = user.EmailVerified,
            TwoFactorEnabled = user.TwoFactorEnabled,
            RequiresPasswordReset = user.RequiresPasswordReset,
        };

    private AuthResponse BuildAuthResponse(
        User user,
        TenantType tenantType,
        IReadOnlyList<string> roles,
        string accessToken,
        string refreshToken,
        DateTime accessExpiresAt)
    {
        var base_ = BuildAuthResponsePlaceholder(user, tenantType, roles);
        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresIn = (int)Math.Max(0, (accessExpiresAt - DateTime.UtcNow).TotalSeconds),
            UserId = base_.UserId,
            Email = base_.Email,
            FullName = base_.FullName,
            FullNameEn = base_.FullNameEn,
            TenantId = base_.TenantId,
            TenantType = base_.TenantType,
            Roles = base_.Roles,
            Locale = base_.Locale,
            EmailVerified = base_.EmailVerified,
            TwoFactorEnabled = base_.TwoFactorEnabled,
            RequiresPasswordReset = base_.RequiresPasswordReset,
        };
    }

    private async Task IssueVerificationAndNotifyAsync(User user, string correlationId, CancellationToken ct)
    {
        var issue = await _verification.IssueAsync(user.Id, correlationId, ct).ConfigureAwait(false);
        if (!issue.Success || string.IsNullOrWhiteSpace(issue.PlaintextToken))
        {
            return;
        }
        var link = _linkBuilder.BuildVerificationLink(issue.PlaintextToken!);
        try
        {
            await _notifications.SendEmailVerificationAsync(
                new IdentityNotificationRecipient(
                    TenantId: user.TenantId,
                    UserId: user.Id,
                    Email: user.Email,
                    FullName: user.FullName,
                    Locale: user.Locale),
                link,
                correlationId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fire-and-forget per audit contract — don't fail registration.
            _logger.LogWarning(ex, "Failed to dispatch email verification to user {UserId}", user.Id);
        }
    }

    private static string InferDeviceName(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "unknown";
        return userAgent.Length <= 200 ? userAgent : userAgent[..200];
    }
}

/// <summary>
/// T078 — Pluggable builder for the email-verification link the frontend
/// lands on. In production, backed by the <c>Identity:VerificationBaseUrl</c>
/// config; tests can stub this to capture the token without standing up
/// the frontend.
/// </summary>
public interface IVerificationLinkBuilder
{
    string BuildVerificationLink(string plaintextToken);
}

public sealed class VerificationLinkBuilder : IVerificationLinkBuilder
{
    private readonly string _baseUrl;

    public VerificationLinkBuilder(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public string BuildVerificationLink(string plaintextToken)
        => $"{_baseUrl}/verify-email?token={Uri.EscapeDataString(plaintextToken)}";
}

/// <summary>
/// Extension on <see cref="ITokenService"/> that exposes the same
/// SHA-256 hashing used by <c>JwtTokenService.GenerateRefreshToken</c>
/// so <c>AuthService</c> can look tokens up by hash.
/// </summary>
internal static class JwtTokenServiceExtensions
{
    public static string HashRefreshToken(string plaintextToken)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintextToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
