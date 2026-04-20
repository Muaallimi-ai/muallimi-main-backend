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
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T154 — US6: Operator impersonation with audit trail.
///
/// <c>StartAsync</c> — super-admin / platform-operator elevates into a
/// target user for at most 1 hour. Issues a special access token whose
/// <c>impersonating</c> claim carries the operator id, impersonation
/// session id, and expiry. Persists an <see cref="ImpersonationSession"/>
/// row.
///
/// <c>EndAsync</c> — explicitly terminates an active session, marking
/// <see cref="ImpersonationSession.EndedAt"/>. Calling this after the
/// 1-hour window is a no-op (already expired).
///
/// <c>ExpireStaleSessionsAsync</c> — called by a hosted background
/// service (registered in DI) to mark sessions whose
/// <see cref="ImpersonationSession.ExpiresAt"/> has passed and emit
/// <c>impersonation_expired</c> audit events.
/// </summary>
public interface IImpersonationService
{
    Task<ImpersonationOutcome> StartAsync(StartImpersonationCommand cmd, CancellationToken ct = default);
    Task<ImpersonationOutcome> EndAsync(EndImpersonationCommand cmd, CancellationToken ct = default);
    Task ExpireStaleSessionsAsync(CancellationToken ct = default);
}

public sealed record ImpersonationOutcome(
    bool Success,
    int HttpStatus,
    string Message,
    ImpersonationStartedResponse? Payload = null,
    string? ErrorCode = null)
{
    public static ImpersonationOutcome Ok(ImpersonationStartedResponse payload)
        => new(true, 200, "impersonation_started", Payload: payload);

    public static ImpersonationOutcome Ended()
        => new(true, 200, "impersonation_ended");

    public static ImpersonationOutcome Fail(int status, string code, string message)
        => new(false, status, message, ErrorCode: code);
}

public sealed record ImpersonationStartedResponse(
    string ImpersonationSessionId,
    string AccessToken,
    int ExpiresIn,
    string TargetUserId,
    string TargetFullName,
    string ExpiresAt);

public sealed class ImpersonationService : IImpersonationService
{
    private readonly MuallimiDbContext _db;
    private readonly ITokenService _tokens;
    private readonly AuditEventEmitter _audit;
    private readonly ILogger<ImpersonationService> _logger;

    public ImpersonationService(
        MuallimiDbContext db,
        ITokenService tokens,
        AuditEventEmitter audit,
        ILogger<ImpersonationService> logger)
    {
        _db = db;
        _tokens = tokens;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ImpersonationOutcome> StartAsync(StartImpersonationCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return ImpersonationOutcome.Fail(400, "reason_required",
                "يجب إدخال سبب الانتحال / Reason is required.");
        }

        var target = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.TargetUserId && u.Status != UserStatus.Archived, ct)
            .ConfigureAwait(false);

        if (target is null)
        {
            return ImpersonationOutcome.Fail(404, "target_not_found",
                "المستخدم المستهدف غير موجود / Target user not found.");
        }

        var targetTenant = await _db.IdentityTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == target.TenantId, ct)
            .ConfigureAwait(false);

        if (targetTenant is null)
        {
            return ImpersonationOutcome.Fail(422, "target_tenant_not_found",
                "مستأجر المستخدم المستهدف غير موجود / Target user tenant not found.");
        }

        var session = new ImpersonationSession
        {
            Id = Guid.NewGuid(),
            ImpersonatorId = cmd.ActorUserId,
            TargetUserId = cmd.TargetUserId,
            TargetTenantId = target.TenantId,
            Reason = cmd.Reason,
            StartedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(ImpersonationSession.DefaultMaxDurationHours),
            CorrelationId = cmd.CorrelationId,
        };

        _db.IdentityImpersonationSessions.Add(session);

        // Resolve target roles for the impersonation token.
        var roleNames = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == target.Id && ur.RevokedAt == null)
            .Join(_db.IdentityRoles.IgnoreQueryFilters(),
                ur => ur.RoleId,
                r => r.Id,
                (_, r) => r.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var impersonationClaim = new Muallimi.Application.Identity.Services.ImpersonationClaim(
            By: cmd.ActorUserId.ToString("D"),
            Session: session.Id.ToString("D"),
            ExpiresAt: session.ExpiresAt);

        // Issue an impersonation token reflecting the TARGET user's identity.
        // A synthetic session id is used (the impersonation session id itself).
        var accessToken = _tokens.GenerateAccessToken(
            target,
            targetTenant.Type,
            roleNames,
            session.Id,        // session_id claim = impersonation session id
            impersonationClaim);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = "Impersonation",
            Action = "impersonation_started",
            ActorId = cmd.ActorUserId.ToString("D"),
            TenantId = cmd.ActorTenantId.ToString("D"),
            TargetType = "User",
            TargetId = cmd.TargetUserId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
            Reason = cmd.Reason,
            ImpersonationSessionId = session.Id.ToString("D"),
        });

        return ImpersonationOutcome.Ok(new ImpersonationStartedResponse(
            ImpersonationSessionId: session.Id.ToString("D"),
            AccessToken: accessToken.Token,
            ExpiresIn: (int)(session.ExpiresAt - DateTime.UtcNow).TotalSeconds,
            TargetUserId: target.Id.ToString("D"),
            TargetFullName: target.FullName ?? string.Empty,
            ExpiresAt: session.ExpiresAt.ToString("O")));
    }

    public async Task<ImpersonationOutcome> EndAsync(EndImpersonationCommand cmd, CancellationToken ct = default)
    {
        var session = await _db.IdentityImpersonationSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == cmd.ImpersonationSessionId, ct)
            .ConfigureAwait(false);

        if (session is null)
        {
            return ImpersonationOutcome.Fail(404, "session_not_found",
                "جلسة الانتحال غير موجودة / Impersonation session not found.");
        }

        // Verify the caller owns this session.
        if (session.ImpersonatorId != cmd.ActorUserId)
        {
            return ImpersonationOutcome.Fail(403, "not_session_owner",
                "غير مصرح لك بإنهاء هذه الجلسة / Not authorized to end this session.");
        }

        var alreadyEnded = session.EndedAt.HasValue;
        session.End();

        if (!alreadyEnded)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _audit.Emit(new AuditEvent
        {
            EventCategory = "Impersonation",
            Action = "impersonation_ended",
            ActorId = cmd.ActorUserId.ToString("D"),
            TenantId = session.TargetTenantId.ToString("D"),
            TargetType = "ImpersonationSession",
            TargetId = session.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
            Reason = session.Reason,
            ImpersonationSessionId = session.Id.ToString("D"),
        });

        return ImpersonationOutcome.Ended();
    }

    public async Task ExpireStaleSessionsAsync(CancellationToken ct = default)
    {
        var expired = await _db.IdentityImpersonationSessions.IgnoreQueryFilters()
            .Where(s => s.EndedAt == null && s.ExpiresAt <= DateTime.UtcNow)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var s in expired)
        {
            s.End();
            _audit.Emit(new AuditEvent
            {
                EventCategory = "Impersonation",
                Action = "impersonation_expired",
                ActorId = s.ImpersonatorId.ToString("D"),
                TenantId = s.TargetTenantId.ToString("D"),
                TargetType = "ImpersonationSession",
                TargetId = s.Id.ToString("D"),
                Outcome = "succeeded",
                CorrelationId = s.CorrelationId,
                Reason = "Session expired after 1 hour.",
                ImpersonationSessionId = s.Id.ToString("D"),
            });
        }

        if (expired.Count > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Expired {Count} stale impersonation sessions.", expired.Count);
        }
    }
}
