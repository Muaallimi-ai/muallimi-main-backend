using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Audit;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Identity.Cryptography;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T131 — 2FA enrollment management: StartEnrollment, VerifyEnrollment,
/// Disable, VerifyTotpCodeAsync (for login). Persists <see cref="TwoFactorSecret"/>
/// with the TOTP secret + recovery codes encrypted at rest via AES-GCM.
///
/// TempSecret is stored in a <see cref="PendingTwoFactorSecret"/> in-process
/// in-memory only during the enrolment window; after VerifyEnrollment it is
/// encrypted and persisted.
/// </summary>
public interface ITwoFactorManagementService
{
    Task<TwoFactorEnableOutcome> StartEnrollmentAsync(EnableTwoFactorCommand cmd, CancellationToken ct = default);
    Task<TwoFactorVerifyOutcome> VerifyEnrollmentAsync(VerifyTwoFactorCommand cmd, CancellationToken ct = default);
    Task<SelfServiceResult> DisableAsync(DisableTwoFactorCommand cmd, CancellationToken ct = default);
    Task<bool> VerifyTotpCodeAsync(Guid userId, string code, CancellationToken ct = default);
    Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default);
}

public sealed record TwoFactorEnableOutcome(
    bool Success,
    int HttpStatus,
    string Message,
    string? QrUri = null,
    string? TempSecret = null,
    IReadOnlyList<ApiResponseError>? Errors = null,
    string? ErrorCode = null);

public sealed record TwoFactorVerifyOutcome(
    bool Success,
    int HttpStatus,
    string Message,
    IReadOnlyList<string>? RecoveryCodes = null,
    IReadOnlyList<ApiResponseError>? Errors = null,
    string? ErrorCode = null);

public sealed class TwoFactorManagementService : ITwoFactorManagementService
{
    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly ITwoFactorService _totp;
    private readonly IAesEncryptor _aes;
    private readonly AuditEventEmitter _audit;
    private readonly ILogger<TwoFactorManagementService> _logger;

    // In-memory staging area: userId → (base32 secret, issuedAt)
    // This avoids persisting unverified secrets. Entries expire after 10 min.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Secret, DateTime Issued)> _pending
        = new();
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(10);

    public TwoFactorManagementService(
        MuallimiDbContext db,
        IPasswordService passwords,
        ITwoFactorService totp,
        IAesEncryptor aes,
        AuditEventEmitter audit,
        ILogger<TwoFactorManagementService> logger)
    {
        _db = db;
        _passwords = passwords;
        _totp = totp;
        _aes = aes;
        _audit = audit;
        _logger = logger;
    }

    // ── Start enrollment ───────────────────────────────────────────────

    public async Task<TwoFactorEnableOutcome> StartEnrollmentAsync(
        EnableTwoFactorCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new TwoFactorEnableOutcome(false, 404, "المستخدم غير موجود.", ErrorCode: "user_not_found");
        }
        if (user.TwoFactorEnabled)
        {
            return new TwoFactorEnableOutcome(false, 409, "التحقق بخطوتين مفعّل بالفعل.", ErrorCode: "already_enabled");
        }
        if (user.AccountType == AccountType.Managed)
        {
            return new TwoFactorEnableOutcome(false, 400, "الحسابات المُدارة لا تدعم التحقق بخطوتين.", ErrorCode: "managed_not_supported");
        }

        var identifier = user.Email ?? user.Username ?? user.Id.ToString("D");
        var enrolment = _totp.GenerateEnrolment(identifier);

        // Stage the temp secret (not persisted until VerifyEnrollment succeeds)
        _pending[cmd.UserId] = (enrolment.Base32Secret, DateTime.UtcNow);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.TwoFactorEnabled.ToString(),
            ActorId = cmd.UserId.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "2fa_enrollment_started",
            TargetType = "User",
            TargetId = cmd.UserId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return new TwoFactorEnableOutcome(
            true, 200,
            "بدأ التسجيل في التحقق بخطوتين",
            QrUri: enrolment.QrProvisioningUri,
            TempSecret: enrolment.Base32Secret);
    }

    // ── Verify enrollment ──────────────────────────────────────────────

    public async Task<TwoFactorVerifyOutcome> VerifyEnrollmentAsync(
        VerifyTwoFactorCommand cmd, CancellationToken ct = default)
    {
        if (!_pending.TryGetValue(cmd.UserId, out var staged)
            || DateTime.UtcNow - staged.Issued > PendingTtl)
        {
            _pending.TryRemove(cmd.UserId, out _);
            return new TwoFactorVerifyOutcome(false, 400, "لم يتم بدء تسجيل التحقق بخطوتين أو انتهت مهلته.", ErrorCode: "enrolment_not_started");
        }

        if (!_totp.VerifyTotp(staged.Secret, cmd.Code))
        {
            return new TwoFactorVerifyOutcome(false, 400, "رمز التحقق غير صحيح.", ErrorCode: "invalid_totp_code");
        }

        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return new TwoFactorVerifyOutcome(false, 404, "المستخدم غير موجود.", ErrorCode: "user_not_found");
        }

        var recoveryCodes = _totp.GenerateRecoveryCodes(10);
        var encryptedSecret = _aes.Encrypt(staged.Secret);
        var encryptedCodes = _aes.Encrypt(_totp.SerializeRecoveryCodes(recoveryCodes));

        // Remove any existing secret row (re-enrollment)
        var existing = await _db.IdentityTwoFactorSecrets
            .FirstOrDefaultAsync(s => s.UserId == cmd.UserId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _db.IdentityTwoFactorSecrets.Remove(existing);
        }
        _db.IdentityTwoFactorSecrets.Add(new TwoFactorSecret
        {
            Id = Guid.NewGuid(),
            UserId = cmd.UserId,
            Secret = encryptedSecret,
            RecoveryCodes = encryptedCodes,
            EnabledAt = DateTime.UtcNow,
        });

        user.TwoFactorEnabled = true;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _pending.TryRemove(cmd.UserId, out _);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.TwoFactorEnabled.ToString(),
            ActorId = cmd.UserId.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "2fa_enabled",
            TargetType = "User",
            TargetId = cmd.UserId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return new TwoFactorVerifyOutcome(true, 200, "تم تفعيل التحقق بخطوتين", RecoveryCodes: recoveryCodes);
    }

    // ── Disable ────────────────────────────────────────────────────────

    public async Task<SelfServiceResult> DisableAsync(
        DisableTwoFactorCommand cmd, CancellationToken ct = default)
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
        if (!user.TwoFactorEnabled)
        {
            return SelfServiceResult.Fail(400, "not_enabled", "التحقق بخطوتين غير مفعّل.");
        }

        var secret = await _db.IdentityTwoFactorSecrets
            .FirstOrDefaultAsync(s => s.UserId == cmd.UserId, ct).ConfigureAwait(false);
        if (secret is not null)
        {
            _db.IdentityTwoFactorSecrets.Remove(secret);
        }
        user.TwoFactorEnabled = false;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.TwoFactorEnabled.ToString(),
            ActorId = cmd.UserId.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "2fa_disabled",
            TargetType = "User",
            TargetId = cmd.UserId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return SelfServiceResult.Ok("تم تعطيل التحقق بخطوتين.");
    }

    // ── Verify TOTP for login ──────────────────────────────────────────

    public async Task<bool> VerifyTotpCodeAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var secret = await _db.IdentityTwoFactorSecrets
            .FirstOrDefaultAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        if (secret is null) return false;

        var base32 = _aes.Decrypt(secret.Secret);
        return _totp.VerifyTotp(base32, code);
    }

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        return user?.TwoFactorEnabled ?? false;
    }
}
