using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Audit;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Credentials;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Application.Identity.Services;
using Muallimi.Application.Identity.Validators;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T129 — Password self-service: forgot-password + reset-password + change-password (US4).
/// Each write path also revokes all other active sessions to kill stale tokens.
/// </summary>
public interface IPasswordResetService
{
    Task<SelfServiceResult> ForgotPasswordAsync(ForgotPasswordCommand cmd, CancellationToken ct = default);
    Task<SelfServiceResult> ResetPasswordAsync(ResetPasswordCommand cmd, CancellationToken ct = default);

    /// <summary>T130 — US4 change-password: verifies current password, updates hash,
    /// revokes all other sessions, emits audit, sends notification.</summary>
    Task<SelfServiceResult> ChangePasswordAsync(ChangePasswordCommand cmd, Guid currentSessionId, CancellationToken ct = default);
}

public sealed record SelfServiceResult(
    bool Success,
    int HttpStatus,
    string Message,
    IReadOnlyList<ApiResponseError>? Errors = null,
    string? ErrorCode = null)
{
    public static SelfServiceResult Ok(string message) => new(true, 200, message);
    public static SelfServiceResult Fail(int status, string code, string message)
        => new(false, status, message,
            Errors: new[] { new ApiResponseError { Code = code, Message = message } },
            ErrorCode: code);
}

public interface IPasswordResetLinkBuilder
{
    string BuildResetLink(string plaintextToken);
}

public sealed class PasswordResetLinkBuilder : IPasswordResetLinkBuilder
{
    private readonly string _baseUrl;
    public PasswordResetLinkBuilder(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');
    public string BuildResetLink(string plaintextToken)
        => $"{_baseUrl}/reset-password?token={Uri.EscapeDataString(plaintextToken)}";
}

public sealed class PasswordResetService : IPasswordResetService
{
    private const int TokenTtlHours = 1;
    private static readonly TimeSpan TokenTtl = TimeSpan.FromHours(TokenTtlHours);

    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly ISessionService _sessions;
    private readonly ISessionCascadeService _sessionCascade;
    private readonly AuditEventEmitter _audit;
    private readonly IIdentityNotificationSender _notifications;
    private readonly IPasswordResetLinkBuilder _linkBuilder;
    private readonly IRateLimitService _rateLimits;
    private readonly IPasswordStrengthValidator _passwordStrength;
    private readonly ICredentialAuditWriter _credentialAudit;
    private readonly IManagerReAuthService _reauth;
    private readonly Muallimi.Api.Identity.Credentials.IChildCredentialNotifier _childNotifier;
    private readonly ILogger<PasswordResetService> _logger;

    /// <summary>
    /// Rate-limit policy for the change-password endpoint:
    /// 5 attempts per 15 minutes, keyed per user. Closes the
    /// brute-force window on the current-password verification step.
    /// </summary>
    private const int ChangePasswordMaxAttempts = 5;
    private static readonly TimeSpan ChangePasswordWindow = TimeSpan.FromMinutes(15);
    private const string ChangePasswordScope = "change-password-user";

    public PasswordResetService(
        MuallimiDbContext db,
        IPasswordService passwords,
        ISessionService sessions,
        ISessionCascadeService sessionCascade,
        AuditEventEmitter audit,
        IIdentityNotificationSender notifications,
        IPasswordResetLinkBuilder linkBuilder,
        IRateLimitService rateLimits,
        IPasswordStrengthValidator passwordStrength,
        ICredentialAuditWriter credentialAudit,
        IManagerReAuthService reauth,
        Muallimi.Api.Identity.Credentials.IChildCredentialNotifier childNotifier,
        ILogger<PasswordResetService> logger)
    {
        _db = db;
        _passwords = passwords;
        _sessions = sessions;
        _sessionCascade = sessionCascade;
        _audit = audit;
        _notifications = notifications;
        _linkBuilder = linkBuilder;
        _rateLimits = rateLimits;
        _passwordStrength = passwordStrength;
        _credentialAudit = credentialAudit;
        _reauth = reauth;
        _childNotifier = childNotifier;
        _logger = logger;
    }

    // ── Forgot password ────────────────────────────────────────────────

    public async Task<SelfServiceResult> ForgotPasswordAsync(ForgotPasswordCommand cmd, CancellationToken ct = default)
    {
        var normalized = AuthService.NormalizeEmail(cmd.Email);
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);

        // Always return success to avoid email enumeration.
        if (user is null || user.Status == UserStatus.Archived)
        {
            return SelfServiceResult.Ok("إذا كان البريد مسجلاً ستصلك رسالة.");
        }

        // Expire any outstanding tokens for this user
        var old = await _db.IdentityPasswordResetTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == user.Id && t.UsedAt == null && t.ExpiresAt > DateTime.UtcNow)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in old)
        {
            t.UsedAt = DateTime.UtcNow; // soft-expire the previous token
        }

        var (plaintext, hash) = GenerateToken();
        _db.IdentityPasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = hash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(TokenTtl),
            IpAddress = cmd.IpAddress,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.PasswordReset.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "reset_requested",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        var link = _linkBuilder.BuildResetLink(plaintext);
        try
        {
            await _notifications.SendPasswordResetAsync(
                new IdentityNotificationRecipient(
                    TenantId: user.TenantId,
                    UserId: user.Id,
                    Email: user.Email!,
                    FullName: user.FullName,
                    Locale: user.Locale),
                link,
                cmd.CorrelationId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch password_reset notification (user {UserId})", user.Id);
        }

        return SelfServiceResult.Ok("إذا كان البريد مسجلاً ستصلك رسالة.");
    }

    // ── Reset password ─────────────────────────────────────────────────

    public async Task<SelfServiceResult> ResetPasswordAsync(ResetPasswordCommand cmd, CancellationToken ct = default)
    {
        var hash = HashToken(cmd.Token);
        var token = await _db.IdentityPasswordResetTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct).ConfigureAwait(false);

        if (token is null || !token.IsUsable)
        {
            return SelfServiceResult.Fail(400, "token_invalid", "رمز إعادة التعيين غير صالح أو منتهي الصلاحية.");
        }

        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == token.UserId, ct).ConfigureAwait(false);
        if (user is null || user.Status == UserStatus.Archived)
        {
            return SelfServiceResult.Fail(400, "token_invalid", "رمز إعادة التعيين غير صالح.");
        }

        token.MarkUsed();
        user.CompletePasswordReset(_passwords.Hash(cmd.NewPassword));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Revoke all sessions after reset
        await _sessions.RevokeAllForUserAsync(user.Id, exceptSessionId: null, ct).ConfigureAwait(false);
        // Add-child redesign: parent password reset cascades to derived child sessions.
        await _sessionCascade.RevokeAllDerivedFromUserAsync(user.Id, ct).ConfigureAwait(false);
        // Phase 9 Phase 3 — invalidate any active re-auth receipt; the password
        // backing the receipt is no longer valid.
        await _reauth.InvalidateAsync(user.Id, ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.PasswordReset.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "reset_completed",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        try
        {
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                await _notifications.SendPasswordChangedAsync(
                    new IdentityNotificationRecipient(
                        TenantId: user.TenantId,
                        UserId: user.Id,
                        Email: user.Email,
                        FullName: user.FullName,
                        Locale: user.Locale),
                    cmd.CorrelationId,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch password_changed after reset (user {UserId})", user.Id);
        }

        return SelfServiceResult.Ok("تمت إعادة تعيين كلمة المرور.");
    }

    // ── Change password (T130 — revokes other sessions) ────────────────

    public async Task<SelfServiceResult> ChangePasswordAsync(
        ChangePasswordCommand cmd, Guid currentSessionId, CancellationToken ct = default)
    {
        // 1) Rate-limit gate. 5 attempts / 15 min per user. Closes the
        //    brute-force window on the current-password verification.
        var rateKey = cmd.UserId.ToString("D");
        var decision = await _rateLimits.IncrementAndCheckAsync(
            ChangePasswordScope, rateKey, ChangePasswordMaxAttempts, ChangePasswordWindow, ct).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            return SelfServiceResult.Fail(429, "rate_limited", "محاولات كثيرة جدًا. حاول مرة أخرى لاحقًا.");
        }

        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct).ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            await EmitChildRejectionIfApplicableAsync(user, cmd, ChildPasswordChangeRejectionReasons.WrongCurrent, ct).ConfigureAwait(false);
            return SelfServiceResult.Fail(401, "invalid_credentials", "بيانات الدخول غير صحيحة.");
        }
        if (user.Status == UserStatus.Locked)
        {
            await EmitChildRejectionIfApplicableAsync(user, cmd, ChildPasswordChangeRejectionReasons.Locked, ct).ConfigureAwait(false);
            return SelfServiceResult.Fail(423, "account_locked", "الحساب مقفول مؤقتًا.");
        }
        if (!_passwords.Verify(cmd.CurrentPassword, user.PasswordHash))
        {
            await EmitChildRejectionIfApplicableAsync(user, cmd, ChildPasswordChangeRejectionReasons.WrongCurrent, ct).ConfigureAwait(false);
            return SelfServiceResult.Fail(401, "invalid_credentials", "بيانات الدخول غير صحيحة.");
        }

        // 2) Password strength gate (zxcvbn ≥ 3) — same threshold as parent registration.
        //    Username + email are passed as user-input dictionary so derived passwords
        //    (e.g. "username2026!") are scored as weak.
        var userInputs = new List<string>();
        if (!string.IsNullOrWhiteSpace(user.Username)) userInputs.Add(user.Username);
        if (!string.IsNullOrWhiteSpace(user.Email)) userInputs.Add(user.Email);
        if (!string.IsNullOrWhiteSpace(user.FullName)) userInputs.Add(user.FullName);
        var strength = _passwordStrength.Evaluate(cmd.NewPassword, userInputs.ToArray());
        if (!strength.IsAcceptable)
        {
            await EmitChildRejectionIfApplicableAsync(user, cmd, ChildPasswordChangeRejectionReasons.Weak, ct).ConfigureAwait(false);
            return SelfServiceResult.Fail(422, "weak_password",
                string.Equals(user.Locale, "en", StringComparison.OrdinalIgnoreCase) ? strength.FeedbackEn : strength.FeedbackAr);
        }

        // 3) Apply via canonical mutation (bumps PasswordHashVersion → optimistic concurrency).
        user.CompletePasswordReset(_passwords.Hash(cmd.NewPassword));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // 4) Successful change → reset the rate-limit counter for this user so a
        //    legitimate change doesn't leave the lockout primed.
        await _rateLimits.ClearLockoutAsync($"{ChangePasswordScope}:{rateKey}", ct).ConfigureAwait(false);

        // 5) Revoke all OTHER sessions (keep the calling session active).
        await _sessions.RevokeAllForUserAsync(
            user.Id,
            exceptSessionId: currentSessionId == Guid.Empty ? null : currentSessionId,
            ct).ConfigureAwait(false);
        // Add-child redesign: parent password change cascades to derived child sessions.
        await _sessionCascade.RevokeAllDerivedFromUserAsync(user.Id, ct).ConfigureAwait(false);
        // Phase 9 Phase 3 — invalidate any active re-auth receipt so a stolen
        // session cannot retain step-up authority across the password change.
        await _reauth.InvalidateAsync(user.Id, ct).ConfigureAwait(false);

        // 6) Identity audit (existing — kept for backwards compatibility with
        //    AuthEventCategory queries) + new credential audit (DB-backed).
        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.PasswordChange.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "password_changed",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });
        if (IsChildSelfChange(user))
        {
            await _credentialAudit.WriteAsync(new CredentialAuditEvent
            {
                Kind = CredentialAuditEventKind.ChildPasswordChangedSelf,
                TenantId = user.TenantId,
                ActorId = user.Id,
                ActorType = CredentialAuditActorTypes.User,
                TargetUserId = user.Id,
                CorrelationId = cmd.CorrelationId,
                IpAddress = cmd.IpAddress,
                UserAgent = cmd.UserAgent,
            }, ct).ConfigureAwait(false);

            // Phase 9 Phase 4: fan out to parent guardians (in-app + email,
            // dashboard banner derived from in-app row). Per-day dedup
            // is enforced inside the notifier, so multiple changes in a
            // day collapse to one row + one email.
            try
            {
                await _childNotifier.NotifyChildPasswordChangedAsync(user, cmd.CorrelationId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fan out child_password_changed notification (user {UserId})", user.Id);
            }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                await _notifications.SendPasswordChangedAsync(
                    new IdentityNotificationRecipient(
                        TenantId: user.TenantId,
                        UserId: user.Id,
                        Email: user.Email,
                        FullName: user.FullName,
                        Locale: user.Locale),
                    cmd.CorrelationId,
                    ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch password_changed notification (user {UserId})", cmd.UserId);
        }

        return SelfServiceResult.Ok("تم تغيير كلمة المرور.");
    }

    private static bool IsChildSelfChange(User user) =>
        user.AccountType == AccountType.Managed
        && string.Equals(user.LoginMethod, "username_password", StringComparison.Ordinal);

    private async Task EmitChildRejectionIfApplicableAsync(
        User? user, ChangePasswordCommand cmd, string reason, CancellationToken ct)
    {
        if (user is null || !IsChildSelfChange(user)) return;
        try
        {
            await _credentialAudit.WriteAsync(new CredentialAuditEvent
            {
                Kind = CredentialAuditEventKind.ChildPasswordChangeRejected,
                TenantId = user.TenantId,
                ActorId = user.Id,
                ActorType = CredentialAuditActorTypes.User,
                TargetUserId = user.Id,
                CorrelationId = cmd.CorrelationId,
                IpAddress = cmd.IpAddress,
                UserAgent = cmd.UserAgent,
                Payload = new { reason },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit child_password_change_rejected (user {UserId}, reason {Reason})", user.Id, reason);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static (string plaintext, string hash) GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plaintext = Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var hash = HashToken(plaintext);
        return (plaintext, hash);
    }

    internal static string HashToken(string token)
    {
        var data = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(data).ToLowerInvariant();
    }
}
