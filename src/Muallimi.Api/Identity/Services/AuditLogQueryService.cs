using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Queries;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T109 — Read-only query service for the admin audit-log page.
/// Queries the Phase 6 append-only <c>AuditEntry</c> table (shared by every
/// phase's audit writer) and filters to identity-relevant actions, then
/// projects to the frontend's <see cref="AdminAuditEntry"/> shape.
/// </summary>
public sealed class AuditLogQueryService : IAuditLogQueryService
{
    // Categories (values from Domain.Identity.Enums.AuthEventCategory) mapped to
    // the action types recorded by AuditTrailWriter / AuditEventEmitter.
    public static readonly IReadOnlyDictionary<string, string[]> CategoryActions
        = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Register"] = new[] { "register_parent", "register_school_admin", "invite_user" },
            ["Login"] = new[] { "login_success", "login_failed", "login_locked", "refresh", "refresh_reuse_detected" },
            ["Logout"] = new[] { "logout" },
            ["PasswordChange"] = new[] { "password_changed", "child_password_regenerated" },
            ["PasswordReset"] = new[] { "reset_requested", "reset_completed", "admin_reset_initiated" },
            ["EmailVerified"] = new[] { "email_verified", "verification_resent", "invitation_accepted" },
            ["RoleGranted"] = new[] { "role_granted" },
            ["RoleRevoked"] = new[] { "role_revoked" },
            ["AccountSuspended"] = new[] { "account_suspended", "child_suspended" },
            ["AccountUnsuspended"] = new[] { "account_unsuspended", "child_unsuspended" },
            ["AccountDeleted"] = new[] { "account_deleted", "self_deleted", "child_deleted" },
            ["Impersonation"] = new[] { "impersonation_started", "impersonation_ended" },
            ["SessionRevoked"] = new[] { "session_revoked_by_admin" },
        };

    private readonly MuallimiDbContext _db;

    public AuditLogQueryService(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<AdminAuditPage> QueryAsync(AuditLogQuery query, CancellationToken ct = default)
    {
        var q = _db.AuditEntries.IgnoreQueryFilters().AsNoTracking();

        if (query.TenantId is { } tenantId)
            q = q.Where(a => a.TenantId == tenantId);
        if (query.ActorId is { } actorId)
            q = q.Where(a => a.ActorId == actorId);
        if (query.TargetId is { } targetId)
            q = q.Where(a => a.TargetId == targetId);
        if (query.From is { } from)
            q = q.Where(a => a.OccurredAt >= from);
        if (query.To is { } to)
            q = q.Where(a => a.OccurredAt <= to);

        string[]? actionFilter = null;
        if (!string.IsNullOrWhiteSpace(query.Category)
            && CategoryActions.TryGetValue(query.Category!, out var mapped))
        {
            actionFilter = mapped;
        }
        if (actionFilter is not null)
        {
            q = q.Where(a => actionFilter.Contains(a.ActionType));
        }

        var cursor = DecodeCursor(query.Cursor);
        if (cursor is { } c)
        {
            q = q.Where(a =>
                a.OccurredAt < c.OccurredAt
                || (a.OccurredAt == c.OccurredAt && a.AuditEntryId.CompareTo(c.Id) < 0));
        }

        var pageSize = Math.Clamp(query.Limit, 1, 200);
        var rows = await q
            .OrderByDescending(a => a.OccurredAt)
            .ThenByDescending(a => a.AuditEntryId)
            .Take(pageSize + 1)
            .ToListAsync(ct).ConfigureAwait(false);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            var last = rows[pageSize - 1];
            nextCursor = EncodeCursor(last.OccurredAt, last.AuditEntryId);
            rows = rows.Take(pageSize).ToList();
        }

        var entries = rows
            .Where(r => string.IsNullOrWhiteSpace(query.Outcome)
                || string.Equals(ExtractOutcome(r.Payload), query.Outcome, StringComparison.OrdinalIgnoreCase))
            .Select(r => new AdminAuditEntry
            {
                Id = r.AuditEntryId.ToString("D"),
                ActorId = r.ActorId.ToString("D"),
                TenantId = r.TenantId.ToString("D"),
                Action = r.ActionType,
                TargetType = r.TargetType ?? string.Empty,
                TargetId = r.TargetId?.ToString("D") ?? string.Empty,
                Outcome = ExtractOutcome(r.Payload),
                Category = DeriveCategory(r.ActionType),
                CorrelationId = r.CorrelationId,
                Reason = ExtractReason(r.Payload),
                OccurredAt = r.OccurredAt,
            })
            .ToList();

        return new AdminAuditPage
        {
            Entries = entries,
            NextCursor = nextCursor,
            TotalCountEstimate = nextCursor is null ? entries.Count : entries.Count + 1,
        };
    }

    private static string DeriveCategory(string actionType)
    {
        foreach (var kvp in CategoryActions)
        {
            if (kvp.Value.Contains(actionType)) return kvp.Key;
        }
        return "Other";
    }

    private static string ExtractOutcome(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "succeeded";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String)
            {
                return o.GetString() ?? "succeeded";
            }
        }
        catch { /* payload isn't JSON */ }
        return "succeeded";
    }

    private static string? ExtractReason(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                return r.GetString();
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private static (DateTime OccurredAt, Guid Id)? DecodeCursor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var bytes = Convert.FromBase64String(raw);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            var parts = text.Split('|');
            if (parts.Length != 2) return null;
            if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var occurred)) return null;
            if (!Guid.TryParseExact(parts[1], "N", out var id)) return null;
            return (occurred, id);
        }
        catch { return null; }
    }

    private static string EncodeCursor(DateTime occurredAt, Guid id)
    {
        var payload = $"{occurredAt.ToUniversalTime():O}|{id:N}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
    }
}
