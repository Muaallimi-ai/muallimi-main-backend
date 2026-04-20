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
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Application.Identity.Services;
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
    private readonly AuditEventEmitter _audit;
    private readonly IIdentityNotificationSender _notifications;
    private readonly IPasswordResetLinkBuilder _linkBuilder;
    private readonly ILogger<PasswordResetService> _logger;

    public PasswordResetService(
        MuallimiDbContext db,
        IPasswordService passwords,
        ISessionService sessions,
        AuditEventEmitter audit,
        IIdentityNotificationSender notifications,
        IPasswordResetLinkBuilder linkBuilder,
        ILogger<PasswordResetService> logger)
    {
        _db = db;
        _passwords = passwords;
        _sessions = sessions;
        _audit = audit;
        _notifications = notifications;
        _linkBuilder = linkBuilder;
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
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct).ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            return SelfServiceResult.Fail(401, "invalid_credentials", "بيانات الدخول غير صحيحة.");
        }
        if (!_passwords.Verify(cmd.CurrentPassword, user.PasswordHash))
        {
            return SelfServiceResult.Fail(401, "invalid_credentials", "بيانات الدخول غير صحيحة.");
        }

        user.CompletePasswordReset(_passwords.Hash(cmd.NewPassword));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Revoke all OTHER sessions (keep the calling session active)
        await _sessions.RevokeAllForUserAsync(
            user.Id,
            exceptSessionId: currentSessionId == Guid.Empty ? null : currentSessionId,
            ct).ConfigureAwait(false);

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
