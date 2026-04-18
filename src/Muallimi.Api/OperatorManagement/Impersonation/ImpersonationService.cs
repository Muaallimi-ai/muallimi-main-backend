using System.Collections.Concurrent;
using System.Security.Cryptography;
using Muallimi.Api.Compliance.AuditTrail;

namespace Muallimi.Api.OperatorManagement.Impersonation;

/// <summary>
/// T102 — Operator impersonation with short-lived tokens and audit trail.
/// Tokens are opaque, random identifiers held in-memory for 15 minutes.
/// Ending a session writes the duration and action count to the audit log.
/// </summary>
public sealed class ImpersonationService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private static readonly ConcurrentDictionary<string, ImpersonationSession> _sessions = new();
    private readonly AuditTrailWriter _audit;

    public ImpersonationService(AuditTrailWriter audit)
    {
        _audit = audit;
    }

    public async Task<ImpersonationStartResult> StartAsync(
        Guid operatorId,
        Guid targetTenantId,
        string targetRole,
        Guid? targetUserId,
        string reason,
        string correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason is required for impersonation audit.", nameof(reason));
        }

        var token = NewToken();
        var now = DateTime.UtcNow;
        var session = new ImpersonationSession
        {
            Token = token,
            OperatorId = operatorId,
            TargetTenantId = targetTenantId,
            TargetRole = targetRole,
            TargetUserId = targetUserId,
            StartedAt = now,
            ExpiresAt = now.Add(TokenLifetime),
            Reason = reason,
            CorrelationId = correlationId,
        };
        _sessions[token] = session;

        var auditId = Guid.NewGuid();
        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = targetTenantId,
            ActorId = operatorId,
            ActorType = "operator",
            TargetId = targetUserId,
            TargetType = $"impersonation:{targetRole}",
            ActionType = "operator.impersonation.started",
            Payload = new { reason, target_role = targetRole, target_tenant_id = targetTenantId, expires_at = session.ExpiresAt },
            CorrelationId = correlationId,
        }, ct);

        return new ImpersonationStartResult(token, session.ExpiresAt, auditId);
    }

    public async Task<ImpersonationEndResult?> EndAsync(
        string token,
        string correlationId,
        CancellationToken ct)
    {
        if (!_sessions.TryRemove(token, out var session)) return null;

        var ended = DateTime.UtcNow;
        var duration = (int)Math.Max(0, (ended - session.StartedAt).TotalSeconds);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = session.TargetTenantId,
            ActorId = session.OperatorId,
            ActorType = "operator",
            TargetId = session.TargetUserId,
            TargetType = $"impersonation:{session.TargetRole}",
            ActionType = "operator.impersonation.ended",
            Payload = new
            {
                duration_seconds = duration,
                actions_performed = session.ActionsPerformed,
                started_at = session.StartedAt,
                ended_at = ended,
            },
            CorrelationId = correlationId,
        }, ct);

        return new ImpersonationEndResult(duration, session.ActionsPerformed, Guid.NewGuid());
    }

    public ImpersonationSession? Lookup(string token)
    {
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (DateTime.UtcNow > session.ExpiresAt)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session;
    }

    private static string NewToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed class ImpersonationSession
{
    public required string Token { get; init; }
    public required Guid OperatorId { get; init; }
    public required Guid TargetTenantId { get; init; }
    public required string TargetRole { get; init; }
    public Guid? TargetUserId { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required string Reason { get; init; }
    public required string CorrelationId { get; init; }
    public int ActionsPerformed { get; set; }
}

public sealed record ImpersonationStartResult(string Token, DateTime ExpiresAt, Guid AuditEntryId);
public sealed record ImpersonationEndResult(int DurationSeconds, int ActionsPerformed, Guid AuditEntryId);
