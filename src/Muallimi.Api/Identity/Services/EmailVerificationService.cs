using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Audit;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T072 — Single-use email verification tokens with a 24 h TTL.
///
/// Flow:
///   1. <see cref="IssueAsync"/> generates a 32-byte random token,
///      returns the plaintext to the caller (which hands it to the
///      notification layer), and stores only the SHA-256 hash.
///   2. <see cref="ConsumeAsync"/> hashes the incoming plaintext,
///      looks up the unused + unexpired row, marks it used, flips
///      the owning user's status to <c>Active</c>, and emits the
///      <c>email_verified</c> audit event.
///   3. <see cref="ResendAsync"/> invalidates every outstanding token
///      for the user and issues a fresh one; rate-limit is enforced by
///      the caller.
/// </summary>
public interface IEmailVerificationService
{
    Task<EmailVerificationIssueResult> IssueAsync(
        Guid userId,
        string correlationId,
        CancellationToken ct = default);

    Task<EmailVerificationResult> ConsumeAsync(
        string plaintextToken,
        string correlationId,
        CancellationToken ct = default);

    Task<EmailVerificationResult> ResendAsync(
        string normalizedEmail,
        string correlationId,
        CancellationToken ct = default);
}

public sealed record EmailVerificationIssueResult(
    bool Success,
    string? PlaintextToken,
    Guid TokenId,
    DateTime ExpiresAt,
    string? ErrorCode = null);

public sealed record EmailVerificationResult(
    bool Success,
    string? ErrorCode = null,
    Guid? UserId = null,
    string? Email = null,
    string? FullName = null,
    string? Locale = null,
    Guid? TenantId = null,
    string? PlaintextToken = null);

public sealed class EmailVerificationService : IEmailVerificationService
{
    private readonly MuallimiDbContext _db;
    private readonly AuditEventEmitter _audit;
    private readonly ILogger<EmailVerificationService> _logger;

    public EmailVerificationService(
        MuallimiDbContext db,
        AuditEventEmitter audit,
        ILogger<EmailVerificationService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    public async Task<EmailVerificationIssueResult> IssueAsync(
        Guid userId,
        string correlationId,
        CancellationToken ct = default)
    {
        var (plaintext, hash) = GenerateToken();
        var token = new EmailVerificationToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = hash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(EmailVerificationToken.DefaultTtlHours),
        };
        _db.IdentityEmailVerificationTokens.Add(token);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new EmailVerificationIssueResult(
            Success: true,
            PlaintextToken: plaintext,
            TokenId: token.Id,
            ExpiresAt: token.ExpiresAt);
    }

    public async Task<EmailVerificationResult> ConsumeAsync(
        string plaintextToken,
        string correlationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return new EmailVerificationResult(false, ErrorCode: "token_invalid");
        }
        var hash = HashToken(plaintextToken);
        var token = await _db.IdentityEmailVerificationTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            .ConfigureAwait(false);
        if (token is null || !token.IsUsable)
        {
            return new EmailVerificationResult(false, ErrorCode: "token_invalid");
        }

        var user = await _db.IdentityUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == token.UserId, ct)
            .ConfigureAwait(false);
        if (user is null)
        {
            return new EmailVerificationResult(false, ErrorCode: "user_not_found");
        }

        token.MarkUsed();
        if (user.Status == UserStatus.PendingEmailVerification)
        {
            user.VerifyEmail();
        }
        else
        {
            // Idempotent re-consumption of an unused token for an already-active
            // user is allowed — e.g. a user clicking an old link. Just mark the
            // email as verified for safety and continue.
            user.EmailVerified = true;
            user.EmailVerifiedAt ??= DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.EmailVerified.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "email_verified",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = correlationId,
        });

        return new EmailVerificationResult(
            Success: true,
            UserId: user.Id,
            Email: user.Email,
            FullName: user.FullName,
            Locale: user.Locale,
            TenantId: user.TenantId);
    }

    public async Task<EmailVerificationResult> ResendAsync(
        string normalizedEmail,
        string correlationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return new EmailVerificationResult(false, ErrorCode: "email_invalid");
        }

        var user = await _db.IdentityUsers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct)
            .ConfigureAwait(false);
        if (user is null)
        {
            // Silent success — don't leak whether the email exists. The
            // envelope returns success with no payload so callers can't
            // enumerate registered addresses (SC-009 co-principle).
            return new EmailVerificationResult(Success: true);
        }
        // Allow Active users to resend — they were registered via the payment flow
        // (created directly as Active) and still need to verify their email.
        if (user.Status != UserStatus.PendingEmailVerification && user.EmailVerified)
        {
            // Already verified — no need to send again.
            return new EmailVerificationResult(Success: true, UserId: user.Id);
        }

        // Invalidate every outstanding token for this user.
        var outstanding = await _db.IdentityEmailVerificationTokens
            .IgnoreQueryFilters()
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var t in outstanding)
        {
            t.UsedAt = DateTime.UtcNow;
        }

        var (plaintext, hash) = GenerateToken();
        var fresh = new EmailVerificationToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = hash,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(EmailVerificationToken.DefaultTtlHours),
        };
        _db.IdentityEmailVerificationTokens.Add(fresh);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.EmailVerified.ToString(),
            ActorId = user.Id.ToString("D"),
            TenantId = user.TenantId.ToString("D"),
            Action = "verification_resent",
            TargetType = "User",
            TargetId = user.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = correlationId,
        });

        return new EmailVerificationResult(
            Success: true,
            UserId: user.Id,
            Email: user.Email,
            FullName: user.FullName,
            Locale: user.Locale,
            TenantId: user.TenantId,
            PlaintextToken: plaintext);
    }

    public static (string plaintext, string hash) GenerateToken()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var plaintext = Convert.ToHexString(raw).ToLowerInvariant();
        var hash = HashToken(plaintext);
        return (plaintext, hash);
    }

    public static string HashToken(string plaintext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();
}
