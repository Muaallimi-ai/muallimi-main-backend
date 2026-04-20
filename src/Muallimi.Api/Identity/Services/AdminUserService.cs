using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Compliance.AuditTrail;
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
/// T108 — Admin user-management surface. Orchestrates the 14 admin
/// endpoints (list / detail / invite / grant / revoke / suspend /
/// unsuspend / delete / reset-password / roles / audit / impersonation
/// is US6). Every write path:
///   • enforces role-scope matching (grant Platform only if actor is
///     Platform, School only if actor is Super-admin or school-admin);
///   • blocks privilege-escalation (non-Platform roles cannot grant
///     Platform-scoped roles);
///   • preserves the last-super-admin invariant (cannot delete /
///     suspend / revoke super-admin off the last active super-admin).
/// </summary>
public interface IAdminUserService
{
    Task<AdminOperationResult<AdminUserList>> ListUsersAsync(ListUsersQuery query, CancellationToken ct = default);
    Task<AdminOperationResult<AdminUserDetail>> GetUserAsync(Guid userId, CancellationToken ct = default);
    Task<AdminOperationResult<AdminInvitationResult>> InviteUserAsync(InviteUserCommand cmd, CancellationToken ct = default);
    Task<AdminOperationResult<object>> GrantRoleAsync(GrantRoleCommand cmd, CancellationToken ct = default);
    Task<AdminOperationResult<object>> RevokeRoleAsync(RevokeRoleCommand cmd, CancellationToken ct = default);
    Task<AdminOperationResult<object>> SuspendUserAsync(SuspendUserCommand cmd, CancellationToken ct = default);
    Task<AdminOperationResult<object>> UnsuspendUserAsync(UnsuspendUserCommand cmd, CancellationToken ct = default);
    Task<AdminOperationResult<object>> DeleteUserAsync(DeleteUserCommand cmd, CancellationToken ct = default);
    Task<AdminOperationResult<object>> AdminResetPasswordAsync(AdminResetPasswordCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<AdminRoleDescriptor>> ListRolesAsync(CancellationToken ct = default);
    Task<AdminOperationResult<object>> ChangePasswordAsync(ChangePasswordCommand cmd, CancellationToken ct = default);
    Task<AdminOperationResult<AuthResponseForAcceptance>> AcceptInvitationAsync(AcceptInvitationCommand cmd, CancellationToken ct = default);
}

public sealed record AdminOperationResult<T>(
    bool Success,
    int HttpStatus,
    string Message,
    T? Payload = default,
    IReadOnlyList<ApiResponseError>? Errors = null,
    string? ErrorCode = null)
{
    public static AdminOperationResult<T> Ok(T payload, string message)
        => new(true, 200, message, Payload: payload);
    public static AdminOperationResult<T> Created(T payload, string message)
        => new(true, 201, message, Payload: payload);
    public static AdminOperationResult<T> Fail(int status, string code, string message)
        => new(false, status, message,
            Errors: new[] { new ApiResponseError { Code = code, Message = message } },
            ErrorCode: code);
}

public sealed record AuthResponseForAcceptance(
    Guid UserId,
    Guid TenantId,
    string Email,
    string FullName,
    IReadOnlyList<string> Roles);

public sealed class AdminUserService : IAdminUserService
{
    private static readonly HashSet<string> PlatformRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "super-admin", "platform-operator", "curriculum-admin", "subject-expert",
    };
    private static readonly HashSet<string> SchoolRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "school-admin", "teacher",
    };
    private static readonly HashSet<string> FamilyRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "parent", "student",
    };

    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly AuditEventEmitter _audit;
    private readonly AuditTrailWriter? _auditTrail;
    private readonly IIdentityNotificationSender _notifications;
    private readonly IEmailVerificationService _verification;
    private readonly IInvitationLinkBuilder _linkBuilder;
    private readonly ILogger<AdminUserService> _logger;

    public AdminUserService(
        MuallimiDbContext db,
        IPasswordService passwords,
        AuditEventEmitter audit,
        IIdentityNotificationSender notifications,
        IEmailVerificationService verification,
        IInvitationLinkBuilder linkBuilder,
        ILogger<AdminUserService> logger,
        AuditTrailWriter? auditTrail = null)
    {
        _db = db;
        _passwords = passwords;
        _audit = audit;
        _auditTrail = auditTrail;
        _notifications = notifications;
        _verification = verification;
        _linkBuilder = linkBuilder;
        _logger = logger;
    }

    // ── List ───────────────────────────────────────────────────────────

    public async Task<AdminOperationResult<AdminUserList>> ListUsersAsync(ListUsersQuery query, CancellationToken ct = default)
    {
        var q = _db.IdentityUsers.IgnoreQueryFilters().AsQueryable();
        if (query.TenantId is { } t) q = q.Where(u => u.TenantId == t);
        if (!string.IsNullOrWhiteSpace(query.Status)
            && Enum.TryParse<UserStatus>(query.Status, ignoreCase: true, out var parsedStatus))
        {
            q = q.Where(u => u.Status == parsedStatus);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToLowerInvariant();
            q = q.Where(u =>
                (u.NormalizedEmail != null && u.NormalizedEmail.Contains(s))
                || (u.NormalizedUsername != null && u.NormalizedUsername.Contains(s))
                || u.FullName.ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(query.RoleName))
        {
            var role = query.RoleName.Trim();
            q = q.Where(u => _db.IdentityUserRoles.IgnoreQueryFilters()
                .Any(ur => ur.UserId == u.Id && ur.RevokedAt == null
                    && _db.IdentityRoles.IgnoreQueryFilters()
                        .Any(r => r.Id == ur.RoleId && r.Name == role)));
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var users = await q
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        var userIds = users.Select(u => u.Id).ToList();
        var tenantIds = users.Select(u => u.TenantId).Distinct().ToList();
        var grants = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(ur => userIds.Contains(ur.UserId) && ur.RevokedAt == null)
            .Join(_db.IdentityRoles.IgnoreQueryFilters(),
                ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var tenants = await _db.IdentityTenants.IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        var summaries = users.Select(u => ProjectSummary(u, tenants, grants.Where(g => g.UserId == u.Id).Select(g => g.Name).ToList())).ToList();
        return AdminOperationResult<AdminUserList>.Ok(new AdminUserList
        {
            Users = summaries,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        }, "ok");
    }

    public async Task<AdminOperationResult<AdminUserDetail>> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return AdminOperationResult<AdminUserDetail>.Fail(404, "user_not_found", "المستخدم غير موجود.");
        }
        var tenants = await _db.IdentityTenants.IgnoreQueryFilters()
            .Where(t => t.Id == user.TenantId).ToListAsync(ct).ConfigureAwait(false);
        var roles = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id && ur.RevokedAt == null)
            .Join(_db.IdentityRoles.IgnoreQueryFilters(),
                ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var sessions = await _db.IdentityUserSessions.IgnoreQueryFilters()
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.CreatedAt)
            .Take(20)
            .ToListAsync(ct).ConfigureAwait(false);

        return AdminOperationResult<AdminUserDetail>.Ok(new AdminUserDetail
        {
            User = ProjectSummary(user, tenants, roles),
            Sessions = sessions.Select(s => new AdminSessionSummary
            {
                SessionId = s.Id.ToString("D"),
                IpAddress = s.IpAddress,
                UserAgent = s.UserAgent,
                CreatedAt = s.CreatedAt,
                LastSeenAt = s.LastSeenAt,
                RevokedAt = s.RevokedAt,
            }).ToList(),
            RecentActivity = Array.Empty<AdminAuditEntry>(),
        }, "ok");
    }

    // ── Invite ─────────────────────────────────────────────────────────

    public async Task<AdminOperationResult<AdminInvitationResult>> InviteUserAsync(InviteUserCommand cmd, CancellationToken ct = default)
    {
        if (!PrivilegeEscalationCheck(cmd.ActorRoles, cmd.Roles, out var blockedRole))
        {
            return AdminOperationResult<AdminInvitationResult>.Fail(403, "privilege_escalation",
                $"لا يمكنك منح دور لا تملكه ({blockedRole}).");
        }

        var normalized = cmd.Email.Trim().ToLowerInvariant();
        var taken = await _db.IdentityUsers.IgnoreQueryFilters()
            .AnyAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        if (taken)
        {
            return AdminOperationResult<AdminInvitationResult>.Fail(409, "email_taken", "البريد الإلكتروني مستخدم بالفعل.");
        }

        var tenantId = await ResolveInvitationTenantAsync(cmd, ct).ConfigureAwait(false);
        if (tenantId is null)
        {
            return AdminOperationResult<AdminInvitationResult>.Fail(400, "tenant_required",
                "يجب تحديد المستأجر.");
        }

        var roleEntities = await _db.IdentityRoles.IgnoreQueryFilters()
            .Where(r => cmd.Roles.Contains(r.Name))
            .ToListAsync(ct).ConfigureAwait(false);
        if (roleEntities.Count != cmd.Roles.Count)
        {
            return AdminOperationResult<AdminInvitationResult>.Fail(400, "role_unknown", "دور غير معروف.");
        }
        var tenantEntity = await _db.IdentityTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId.Value, ct).ConfigureAwait(false);
        if (tenantEntity is null)
        {
            return AdminOperationResult<AdminInvitationResult>.Fail(400, "tenant_unknown", "المستأجر غير موجود.");
        }
        if (!ValidateRoleScopeForTenant(roleEntities, tenantEntity.Type, out var scopeMismatch))
        {
            return AdminOperationResult<AdminInvitationResult>.Fail(400, "role_scope_mismatch", scopeMismatch);
        }

        // Random temporary password (unreachable plaintext — the invitee
        // sets their password via the accept-invitation endpoint).
        var tempPlaintext = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId.Value,
            AccountType = AccountType.Personal,
            Email = cmd.Email.Trim(),
            NormalizedEmail = normalized,
            EmailVerified = false,
            FullName = cmd.FullName.Trim(),
            FullNameEn = cmd.FullNameEn?.Trim(),
            Locale = string.IsNullOrWhiteSpace(cmd.Locale) ? "ar" : cmd.Locale,
            Status = UserStatus.PendingEmailVerification,
            PasswordHash = _passwords.Hash(tempPlaintext),
            PasswordChangedAt = DateTime.UtcNow,
            RequiresPasswordReset = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = cmd.ActorUserId,
        };
        user.AssertAccountTypeInvariants();

        _db.IdentityUsers.Add(user);
        foreach (var role in roleEntities)
        {
            _db.IdentityUserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                RoleId = role.Id,
                TenantId = tenantId.Value,
                GrantedBy = cmd.ActorUserId,
                GrantedAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var issue = await _verification.IssueAsync(user.Id, cmd.CorrelationId, ct).ConfigureAwait(false);
        var invitationLink = issue.Success && !string.IsNullOrWhiteSpace(issue.PlaintextToken)
            ? _linkBuilder.BuildInvitationLink(issue.PlaintextToken!)
            : string.Empty;

        await EmitAuditAsync(
            category: AuthEventCategory.Register,
            actorId: cmd.ActorUserId,
            tenantId: cmd.ActorTenantId,
            action: "invite_user",
            targetType: "User",
            targetId: user.Id,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { roles = cmd.Roles, target_tenant = tenantId.Value, outcome = "succeeded" },
            ct).ConfigureAwait(false);

        try
        {
            await _notifications.SendInvitationAsync(
                new IdentityNotificationRecipient(
                    TenantId: user.TenantId,
                    UserId: user.Id,
                    Email: user.Email,
                    FullName: user.FullName,
                    Locale: user.Locale),
                string.Join(",", cmd.Roles),
                invitationLink,
                cmd.CorrelationId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch invitation notification (user {UserId})", user.Id);
        }

        return AdminOperationResult<AdminInvitationResult>.Created(new AdminInvitationResult
        {
            UserId = user.Id.ToString("D"),
            Email = user.Email ?? string.Empty,
            InvitationLink = invitationLink,
            RolesGranted = cmd.Roles,
            ExpiresAt = issue.ExpiresAt,
        }, "تم إرسال الدعوة.");
    }

    // ── Grant / Revoke role ────────────────────────────────────────────

    public async Task<AdminOperationResult<object>> GrantRoleAsync(GrantRoleCommand cmd, CancellationToken ct = default)
    {
        if (!PrivilegeEscalationCheck(cmd.ActorRoles, new[] { cmd.RoleName }, out var blocked))
        {
            return AdminOperationResult<object>.Fail(403, "privilege_escalation",
                $"لا يمكنك منح دور لا تملكه ({blocked}).");
        }
        var target = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.TargetUserId, ct).ConfigureAwait(false);
        if (target is null)
            return AdminOperationResult<object>.Fail(404, "user_not_found", "المستخدم غير موجود.");
        var role = await _db.IdentityRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == cmd.RoleName, ct).ConfigureAwait(false);
        if (role is null)
            return AdminOperationResult<object>.Fail(400, "role_unknown", "الدور غير معروف.");
        var tenant = await _db.IdentityTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == target.TenantId, ct).ConfigureAwait(false);
        if (tenant is null)
            return AdminOperationResult<object>.Fail(400, "tenant_unknown", "المستأجر غير موجود.");
        if (!ValidateRoleScopeForTenant(new[] { role }, tenant.Type, out var reason))
        {
            return AdminOperationResult<object>.Fail(400, "role_scope_mismatch", reason);
        }

        var existing = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(ur => ur.UserId == cmd.TargetUserId && ur.RoleId == role.Id && ur.RevokedAt == null, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return AdminOperationResult<object>.Ok(new { granted = true, alreadyHeld = true }, "الدور ممنوح مسبقًا.");
        }
        _db.IdentityUserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = cmd.TargetUserId,
            RoleId = role.Id,
            TenantId = target.TenantId,
            GrantedBy = cmd.ActorUserId,
            GrantedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(
            category: AuthEventCategory.RoleGranted,
            actorId: cmd.ActorUserId,
            tenantId: cmd.ActorTenantId,
            action: "role_granted",
            targetType: "User",
            targetId: cmd.TargetUserId,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { role = cmd.RoleName, outcome = "succeeded" },
            ct).ConfigureAwait(false);

        return AdminOperationResult<object>.Ok(new { granted = true }, "تم منح الدور.");
    }

    public async Task<AdminOperationResult<object>> RevokeRoleAsync(RevokeRoleCommand cmd, CancellationToken ct = default)
    {
        if (!PrivilegeEscalationCheck(cmd.ActorRoles, new[] { cmd.RoleName }, out var blocked))
        {
            return AdminOperationResult<object>.Fail(403, "privilege_escalation",
                $"لا يمكنك سحب دور لا تملكه ({blocked}).");
        }
        var role = await _db.IdentityRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == cmd.RoleName, ct).ConfigureAwait(false);
        if (role is null)
            return AdminOperationResult<object>.Fail(400, "role_unknown", "الدور غير معروف.");
        var grant = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(ur => ur.UserId == cmd.TargetUserId && ur.RoleId == role.Id && ur.RevokedAt == null, ct).ConfigureAwait(false);
        if (grant is null)
            return AdminOperationResult<object>.Ok(new { revoked = true, alreadyMissing = true }, "الدور غير ممنوح.");

        if (string.Equals(cmd.RoleName, "super-admin", StringComparison.OrdinalIgnoreCase)
            && await WouldLeaveZeroSuperAdminsAsync(cmd.TargetUserId, ct).ConfigureAwait(false))
        {
            return AdminOperationResult<object>.Fail(409, "last_super_admin",
                "لا يمكن سحب دور المشرف الأعلى من آخر حساب.");
        }

        grant.Revoke();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(
            category: AuthEventCategory.RoleRevoked,
            actorId: cmd.ActorUserId,
            tenantId: cmd.ActorTenantId,
            action: "role_revoked",
            targetType: "User",
            targetId: cmd.TargetUserId,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { role = cmd.RoleName, outcome = "succeeded" },
            ct).ConfigureAwait(false);

        return AdminOperationResult<object>.Ok(new { revoked = true }, "تم سحب الدور.");
    }

    // ── Suspend / Unsuspend / Delete ───────────────────────────────────

    public async Task<AdminOperationResult<object>> SuspendUserAsync(SuspendUserCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.TargetUserId, ct).ConfigureAwait(false);
        if (user is null)
            return AdminOperationResult<object>.Fail(404, "user_not_found", "المستخدم غير موجود.");
        if (user.Status == UserStatus.Archived)
            return AdminOperationResult<object>.Fail(409, "user_archived", "الحساب محذوف.");

        if (await IsLastSuperAdminAsync(cmd.TargetUserId, ct).ConfigureAwait(false))
        {
            return AdminOperationResult<object>.Fail(409, "last_super_admin",
                "لا يمكن تعليق آخر حساب مشرف أعلى.");
        }
        user.Suspend();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(
            category: AuthEventCategory.AccountSuspended,
            actorId: cmd.ActorUserId,
            tenantId: cmd.ActorTenantId,
            action: "account_suspended",
            targetType: "User",
            targetId: cmd.TargetUserId,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { reason = cmd.Reason, outcome = "succeeded" },
            ct).ConfigureAwait(false);

        return AdminOperationResult<object>.Ok(new { suspended = true }, "تم تعليق الحساب.");
    }

    public async Task<AdminOperationResult<object>> UnsuspendUserAsync(UnsuspendUserCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.TargetUserId, ct).ConfigureAwait(false);
        if (user is null)
            return AdminOperationResult<object>.Fail(404, "user_not_found", "المستخدم غير موجود.");
        if (user.Status != UserStatus.Suspended)
            return AdminOperationResult<object>.Fail(409, "not_suspended", "الحساب ليس معلّقًا.");

        user.Unsuspend();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(
            category: AuthEventCategory.AccountUnsuspended,
            actorId: cmd.ActorUserId,
            tenantId: cmd.ActorTenantId,
            action: "account_unsuspended",
            targetType: "User",
            targetId: cmd.TargetUserId,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { outcome = "succeeded" },
            ct).ConfigureAwait(false);

        return AdminOperationResult<object>.Ok(new { unsuspended = true }, "تم رفع التعليق.");
    }

    public async Task<AdminOperationResult<object>> DeleteUserAsync(DeleteUserCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.TargetUserId, ct).ConfigureAwait(false);
        if (user is null)
            return AdminOperationResult<object>.Fail(404, "user_not_found", "المستخدم غير موجود.");
        if (user.Status == UserStatus.Archived)
            return AdminOperationResult<object>.Ok(new { deleted = true, alreadyArchived = true }, "الحساب محذوف.");
        if (await IsLastSuperAdminAsync(cmd.TargetUserId, ct).ConfigureAwait(false))
        {
            return AdminOperationResult<object>.Fail(409, "last_super_admin",
                "لا يمكن حذف آخر حساب مشرف أعلى.");
        }

        user.Archive();
        var sessionTokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == cmd.TargetUserId && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in sessionTokens)
        {
            t.MarkFamilyRevoked();
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(
            category: AuthEventCategory.AccountDeleted,
            actorId: cmd.ActorUserId,
            tenantId: cmd.ActorTenantId,
            action: "account_deleted",
            targetType: "User",
            targetId: cmd.TargetUserId,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { outcome = "succeeded" },
            ct).ConfigureAwait(false);

        return AdminOperationResult<object>.Ok(new { deleted = true }, "تم حذف الحساب.");
    }

    public async Task<AdminOperationResult<object>> AdminResetPasswordAsync(AdminResetPasswordCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.TargetUserId, ct).ConfigureAwait(false);
        if (user is null)
            return AdminOperationResult<object>.Fail(404, "user_not_found", "المستخدم غير موجود.");
        if (user.Status == UserStatus.Archived)
            return AdminOperationResult<object>.Fail(409, "user_archived", "الحساب محذوف.");
        if (string.IsNullOrWhiteSpace(user.Email))
            return AdminOperationResult<object>.Fail(409, "no_email", "هذا الحساب لا يحتوي على بريد.");

        user.RequirePasswordReset();
        // Revoke live refresh tokens immediately.
        var liveTokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == cmd.TargetUserId && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in liveTokens)
        {
            t.MarkFamilyRevoked();
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var issue = await _verification.IssueAsync(user.Id, cmd.CorrelationId, ct).ConfigureAwait(false);
        var resetLink = issue.Success && !string.IsNullOrWhiteSpace(issue.PlaintextToken)
            ? _linkBuilder.BuildResetLink(issue.PlaintextToken!)
            : string.Empty;

        await EmitAuditAsync(
            category: AuthEventCategory.PasswordReset,
            actorId: cmd.ActorUserId,
            tenantId: cmd.ActorTenantId,
            action: "admin_reset_initiated",
            targetType: "User",
            targetId: cmd.TargetUserId,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { outcome = "succeeded" },
            ct).ConfigureAwait(false);

        try
        {
            await _notifications.SendPasswordResetAsync(
                new IdentityNotificationRecipient(
                    TenantId: user.TenantId,
                    UserId: user.Id,
                    Email: user.Email,
                    FullName: user.FullName,
                    Locale: user.Locale),
                resetLink,
                cmd.CorrelationId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch admin reset notification (user {UserId})", user.Id);
        }

        return AdminOperationResult<object>.Ok(new { resetInitiated = true }, "تم بدء إعادة تعيين كلمة المرور.");
    }

    // ── Roles reference ────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminRoleDescriptor>> ListRolesAsync(CancellationToken ct = default)
    {
        var roles = await _db.IdentityRoles.IgnoreQueryFilters()
            .OrderBy(r => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        return roles.Select(r => new AdminRoleDescriptor
        {
            Name = r.Name,
            Scope = r.Scope.ToString().ToLowerInvariant(),
            Description = r.Description ?? string.Empty,
            IsSystem = r.IsSystem,
        }).ToList();
    }

    // ── Forced-rotation: change-password ───────────────────────────────

    public async Task<AdminOperationResult<object>> ChangePasswordAsync(ChangePasswordCommand cmd, CancellationToken ct = default)
    {
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.UserId, ct).ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(user.PasswordHash))
        {
            return AdminOperationResult<object>.Fail(401, "invalid_credentials", "بيانات الدخول غير صحيحة.");
        }
        if (!_passwords.Verify(cmd.CurrentPassword, user.PasswordHash))
        {
            return AdminOperationResult<object>.Fail(401, "invalid_credentials", "بيانات الدخول غير صحيحة.");
        }
        user.CompletePasswordReset(_passwords.Hash(cmd.NewPassword));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(
            category: AuthEventCategory.PasswordChange,
            actorId: cmd.UserId,
            tenantId: user.TenantId,
            action: "password_changed",
            targetType: "User",
            targetId: cmd.UserId,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { outcome = "succeeded" },
            ct).ConfigureAwait(false);

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

        return AdminOperationResult<object>.Ok(new { passwordChanged = true }, "تم تغيير كلمة المرور.");
    }

    public async Task<AdminOperationResult<AuthResponseForAcceptance>> AcceptInvitationAsync(AcceptInvitationCommand cmd, CancellationToken ct = default)
    {
        var consume = await _verification.ConsumeAsync(cmd.Token, cmd.CorrelationId, ct).ConfigureAwait(false);
        if (!consume.Success || consume.UserId is null)
        {
            return AdminOperationResult<AuthResponseForAcceptance>.Fail(400,
                consume.ErrorCode ?? "token_invalid", "رمز الدعوة غير صالح.");
        }
        var user = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == consume.UserId.Value, ct).ConfigureAwait(false);
        if (user is null)
        {
            return AdminOperationResult<AuthResponseForAcceptance>.Fail(404, "user_not_found", "المستخدم غير موجود.");
        }

        user.CompletePasswordReset(_passwords.Hash(cmd.NewPassword));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await EmitAuditAsync(
            category: AuthEventCategory.EmailVerified,
            actorId: user.Id,
            tenantId: user.TenantId,
            action: "invitation_accepted",
            targetType: "User",
            targetId: user.Id,
            outcome: "succeeded",
            correlationId: cmd.CorrelationId,
            ipAddress: cmd.IpAddress,
            userAgent: cmd.UserAgent,
            payload: new { outcome = "succeeded" },
            ct).ConfigureAwait(false);

        var roles = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .Where(ur => ur.UserId == user.Id && ur.RevokedAt == null)
            .Join(_db.IdentityRoles.IgnoreQueryFilters(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        return AdminOperationResult<AuthResponseForAcceptance>.Ok(
            new AuthResponseForAcceptance(user.Id, user.TenantId, user.Email ?? string.Empty, user.FullName, roles),
            "تم قبول الدعوة.");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static bool PrivilegeEscalationCheck(
        IReadOnlyList<string> actorRoles,
        IReadOnlyList<string> targetRoles,
        out string? blocked)
    {
        blocked = null;
        var actorSet = new HashSet<string>(actorRoles ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var actorIsSuper = actorSet.Contains("super-admin");
        var actorIsPlatform = actorSet.Overlaps(PlatformRoles);
        foreach (var r in targetRoles ?? Array.Empty<string>())
        {
            if (PlatformRoles.Contains(r))
            {
                // Only super-admin can grant Platform roles. (platform-operator
                // is read-only across tenants per the HTTP contract.)
                if (!actorIsSuper)
                {
                    blocked = r;
                    return false;
                }
            }
            else if (SchoolRoles.Contains(r))
            {
                // super-admin OR school-admin can grant School roles.
                if (!(actorIsSuper || actorSet.Contains("school-admin")))
                {
                    blocked = r;
                    return false;
                }
            }
            else if (FamilyRoles.Contains(r))
            {
                // Only super-admin or parent can grant Family-scoped roles;
                // parent grants happen via US2 not here, so we require
                // platform membership.
                if (!actorIsPlatform)
                {
                    blocked = r;
                    return false;
                }
            }
        }
        return true;
    }

    private static bool ValidateRoleScopeForTenant(IReadOnlyList<Role> roles, TenantType tenantType, out string reason)
    {
        foreach (var r in roles)
        {
            var expected = tenantType switch
            {
                TenantType.Platform => RoleScope.Platform,
                TenantType.School => RoleScope.School,
                TenantType.Family => RoleScope.Family,
                _ => RoleScope.Any,
            };
            if (r.Scope != RoleScope.Any && r.Scope != expected)
            {
                reason = $"الدور {r.Name} غير متوافق مع نوع المستأجر {tenantType}.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private async Task<bool> IsLastSuperAdminAsync(Guid userId, CancellationToken ct)
    {
        var count = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .Join(_db.IdentityRoles.IgnoreQueryFilters(),
                ur => ur.RoleId, r => r.Id, (ur, r) => new { ur, r })
            .Where(x => x.r.Name == "super-admin" && x.ur.RevokedAt == null)
            .Join(_db.IdentityUsers.IgnoreQueryFilters(),
                x => x.ur.UserId, u => u.Id, (x, u) => new { x.ur, u })
            .Where(x => x.u.Status != UserStatus.Archived)
            .CountAsync(ct).ConfigureAwait(false);
        if (count > 1) return false;
        var userHas = await _db.IdentityUserRoles.IgnoreQueryFilters()
            .Join(_db.IdentityRoles.IgnoreQueryFilters(),
                ur => ur.RoleId, r => r.Id, (ur, r) => new { ur, r })
            .AnyAsync(x => x.ur.UserId == userId && x.r.Name == "super-admin" && x.ur.RevokedAt == null, ct).ConfigureAwait(false);
        return userHas;
    }

    private async Task<bool> WouldLeaveZeroSuperAdminsAsync(Guid userId, CancellationToken ct)
    {
        return await IsLastSuperAdminAsync(userId, ct).ConfigureAwait(false);
    }

    private async Task<Guid?> ResolveInvitationTenantAsync(InviteUserCommand cmd, CancellationToken ct)
    {
        if (cmd.TargetTenantId is { } t) return t;
        // Platform invitations default to the Platform tenant.
        var platform = await _db.IdentityTenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(tt => tt.Type == TenantType.Platform, ct).ConfigureAwait(false);
        if (platform is not null
            && cmd.Roles.All(r => PlatformRoles.Contains(r)))
        {
            return platform.Id;
        }
        // School invitations default to the actor's tenant.
        if (cmd.Roles.All(r => SchoolRoles.Contains(r)))
        {
            return cmd.ActorTenantId == Guid.Empty ? null : cmd.ActorTenantId;
        }
        return null;
    }

    private AdminUserSummary ProjectSummary(User u, IReadOnlyList<Tenant> tenants, IReadOnlyList<string> roles)
    {
        var tenant = tenants.FirstOrDefault(t => t.Id == u.TenantId);
        return new AdminUserSummary
        {
            UserId = u.Id.ToString("D"),
            Email = u.Email,
            Username = u.Username,
            FullName = u.FullName,
            FullNameEn = u.FullNameEn,
            TenantId = u.TenantId.ToString("D"),
            TenantType = (tenant?.Type ?? TenantType.Family).ToString().ToLowerInvariant(),
            AccountType = u.AccountType.ToString().ToLowerInvariant(),
            Status = u.Status.ToString().ToLowerInvariant(),
            Locale = u.Locale,
            Roles = roles,
            EmailVerified = u.EmailVerified,
            TwoFactorEnabled = u.TwoFactorEnabled,
            RequiresPasswordReset = u.RequiresPasswordReset,
            LastLoginAt = u.LastLoginAt,
            CreatedAt = u.CreatedAt,
        };
    }

    private async Task EmitAuditAsync(
        AuthEventCategory category,
        Guid actorId,
        Guid tenantId,
        string action,
        string targetType,
        Guid targetId,
        string outcome,
        string correlationId,
        string ipAddress,
        string? userAgent,
        object payload,
        CancellationToken ct)
    {
        _audit.Emit(new AuditEvent
        {
            EventCategory = category.ToString(),
            ActorId = actorId.ToString("D"),
            TenantId = tenantId.ToString("D"),
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString("D"),
            Outcome = outcome,
            CorrelationId = correlationId,
            Reason = null,
        });

        if (_auditTrail is not null)
        {
            try
            {
                await _auditTrail.WriteAsync(new AuditTrailEntry
                {
                    TenantId = tenantId,
                    ActorId = actorId,
                    ActorType = "user",
                    TargetId = targetId,
                    TargetType = targetType,
                    ActionType = action,
                    Payload = payload,
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    CorrelationId = correlationId,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AuditTrailWriter failed for action {Action}", action);
            }
        }
    }
}

/// <summary>
/// T113 — Builder for the invitation + admin-reset links the identity
/// notifications carry. Backed by the <c>Identity:VerificationBaseUrl</c>
/// config.
/// </summary>
public interface IInvitationLinkBuilder
{
    string BuildInvitationLink(string token);
    string BuildResetLink(string token);
}

public sealed class InvitationLinkBuilder : IInvitationLinkBuilder
{
    private readonly string _baseUrl;
    public InvitationLinkBuilder(string baseUrl) => _baseUrl = (baseUrl ?? "http://localhost:3000").TrimEnd('/');
    public string BuildInvitationLink(string token) => $"{_baseUrl}/invitation?token={Uri.EscapeDataString(token)}";
    public string BuildResetLink(string token) => $"{_baseUrl}/reset-password?token={Uri.EscapeDataString(token)}";
}
