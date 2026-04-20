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
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// T089 — User management service powering the parent-children surface
/// (US2). Creates Managed student accounts scoped to the parent's
/// Family tenant, returns credentials exactly once, and supports
/// read / update / regenerate-password / delete.
///
/// Authorization is enforced by the endpoint layer (parent role check +
/// <c>ManagedByUserId == parentUserId</c>); this service still refuses
/// writes to users it does not own so a missing endpoint check cannot
/// silently leak cross-tenant access.
/// </summary>
public interface IUserManagementService
{
    Task<ChildOperationResult<ChildCredentialsOnce>> CreateChildAsync(CreateChildCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<ChildSummary>> ListChildrenAsync(Guid parentUserId, Guid parentTenantId, CancellationToken ct = default);
    Task<ChildDetail?> GetChildAsync(Guid parentUserId, Guid childUserId, CancellationToken ct = default);
    Task<ChildOperationResult<ChildDetail>> UpdateChildAsync(UpdateChildCommand cmd, CancellationToken ct = default);
    Task<ChildOperationResult<ChildCredentialsOnce>> RegenerateChildPasswordAsync(RegenerateChildPasswordCommand cmd, CancellationToken ct = default);
    Task<ChildOperationResult<object>> DeleteChildAsync(DeleteChildCommand cmd, CancellationToken ct = default);

    // US5 — Parent oversight
    Task<ChildOperationResult<object>> SuspendChildAsync(SuspendChildCommand cmd, CancellationToken ct = default);
    Task<ChildOperationResult<object>> UnsuspendChildAsync(UnsuspendChildCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<ChildSessionSummary>> ListChildSessionsAsync(Guid parentUserId, Guid childUserId, CancellationToken ct = default);
    Task<ChildOperationResult<object>> RevokeChildSessionAsync(RevokeChildSessionCommand cmd, CancellationToken ct = default);
    Task<IReadOnlyList<ChildLoginHistoryItem>> GetChildLoginHistoryAsync(Guid parentUserId, Guid childUserId, int limit, CancellationToken ct = default);
}

public sealed record ChildOperationResult<T>(
    bool Success,
    int HttpStatus,
    string Message,
    T? Payload = default,
    IReadOnlyList<ApiResponseError>? Errors = null,
    string? ErrorCode = null)
{
    public static ChildOperationResult<T> Ok(T payload, string message)
        => new(true, 200, message, Payload: payload);

    public static ChildOperationResult<T> Created(T payload, string message)
        => new(true, 201, message, Payload: payload);

    public static ChildOperationResult<T> Fail(int status, string code, string message)
        => new(false, status, message,
            Errors: new[] { new ApiResponseError { Code = code, Message = message } },
            ErrorCode: code);
}

public sealed class UserManagementService : IUserManagementService
{
    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly IUsernameGenerator _usernameGenerator;
    private readonly IChildPasswordGenerator _passwordGenerator;
    private readonly AuditEventEmitter _audit;
    private readonly IIdentityNotificationSender _notifications;
    private readonly ILogger<UserManagementService> _logger;

    public UserManagementService(
        MuallimiDbContext db,
        IPasswordService passwords,
        IUsernameGenerator usernameGenerator,
        IChildPasswordGenerator passwordGenerator,
        AuditEventEmitter audit,
        IIdentityNotificationSender notifications,
        ILogger<UserManagementService> logger)
    {
        _db = db;
        _passwords = passwords;
        _usernameGenerator = usernameGenerator;
        _passwordGenerator = passwordGenerator;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<ChildOperationResult<ChildCredentialsOnce>> CreateChildAsync(CreateChildCommand cmd, CancellationToken ct = default)
    {
        var parent = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.ParentUserId, ct).ConfigureAwait(false);
        if (parent is null || parent.AccountType != AccountType.Personal)
        {
            return ChildOperationResult<ChildCredentialsOnce>.Fail(403, "parent_not_found", "غير مصرّح.");
        }
        if (parent.TenantId != cmd.ParentTenantId)
        {
            return ChildOperationResult<ChildCredentialsOnce>.Fail(403, "tenant_mismatch", "غير مصرّح.");
        }

        string username;
        try
        {
            username = await _usernameGenerator.GenerateAsync(
                cmd.FullName,
                cmd.Birthday.Year,
                cmd.PreferredUsername,
                async (candidate, c) =>
                {
                    var lower = candidate.ToLowerInvariant();
                    return await _db.IdentityUsers.IgnoreQueryFilters()
                        .AnyAsync(u => u.NormalizedUsername == lower, c).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message == "username_unavailable")
        {
            return ChildOperationResult<ChildCredentialsOnce>.Fail(409, "username_unavailable", "اسم المستخدم غير متاح.");
        }

        var plaintextPassword = string.IsNullOrEmpty(cmd.CustomPassword)
            ? _passwordGenerator.Generate(cmd.PasswordLocale)
            : cmd.CustomPassword;

        var childId = Guid.NewGuid();
        var child = new User
        {
            Id = childId,
            TenantId = parent.TenantId,
            AccountType = AccountType.Managed,
            ManagedByUserId = parent.Id,
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            FullName = cmd.FullName.Trim(),
            FullNameEn = cmd.FullNameEn?.Trim(),
            Locale = parent.Locale,
            Status = UserStatus.Active,
            PasswordHash = _passwords.Hash(plaintextPassword),
            PasswordChangedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = parent.Id,
        };
        child.AssertAccountTypeInvariants();

        var metadata = BuildChildMetadata(cmd.Grade, cmd.Gender, cmd.Birthday);

        var studentRole = await _db.IdentityRoles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == "student", ct).ConfigureAwait(false);
        if (studentRole is null)
        {
            return ChildOperationResult<ChildCredentialsOnce>.Fail(500, "role_missing", "دور الطالب غير موجود.");
        }
        var grant = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = child.Id,
            RoleId = studentRole.Id,
            TenantId = parent.TenantId,
            GrantedBy = parent.Id,
            GrantedAt = DateTime.UtcNow,
        };

        _db.IdentityUsers.Add(child);
        _db.IdentityUserRoles.Add(grant);

        // Persist grade/gender/birthday on the family-scoped StudentProfile
        // row so subsequent reads (list/detail) project from the same
        // source of truth the frontend dialog seeds from.
        var now = DateTime.UtcNow;
        var profile = new StudentProfile
        {
            Id = Guid.NewGuid(),
            TenantId = parent.TenantId,
            UserId = child.Id,
            DisplayName = child.FullName,
            CurriculumType = "MOE-EG",
            Grade = cmd.Grade.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PreferredLanguage = child.Locale,
            PlanTier = "free",
            SubjectsEnrolled = "[]",
            ConsentState = "pending",
            Birthday = cmd.Birthday == default ? null : DateOnly.FromDateTime(cmd.Birthday),
            Gender = string.IsNullOrWhiteSpace(cmd.Gender) ? null : cmd.Gender,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.StudentProfiles.Add(profile);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Register.ToString(),
            ActorId = parent.Id.ToString("D"),
            TenantId = parent.TenantId.ToString("D"),
            Action = "child_created",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
            Reason = metadata,
        });

        try
        {
            await _notifications.SendChildCreatedAsync(
                new IdentityNotificationRecipient(
                    TenantId: parent.TenantId,
                    UserId: parent.Id,
                    Email: parent.Email,
                    FullName: parent.FullName,
                    Locale: parent.Locale),
                child.FullName,
                username,
                plaintextPassword,
                cmd.CorrelationId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch child_created notification (child {ChildId})", child.Id);
        }

        var payload = new ChildCredentialsOnce
        {
            UserId = child.Id.ToString("D"),
            Username = username,
            GeneratedPassword = plaintextPassword,
            FullName = child.FullName,
            Grade = cmd.Grade,
            TenantId = parent.TenantId.ToString("D"),
            CreatedAt = child.CreatedAt,
        };
        return ChildOperationResult<ChildCredentialsOnce>.Created(payload, $"تم إنشاء حساب {child.FullName} بنجاح.");
    }

    public async Task<IReadOnlyList<ChildSummary>> ListChildrenAsync(Guid parentUserId, Guid parentTenantId, CancellationToken ct = default)
    {
        var children = await _db.IdentityUsers.IgnoreQueryFilters()
            .Where(u => u.ManagedByUserId == parentUserId
                && u.TenantId == parentTenantId
                && u.AccountType == AccountType.Managed
                && u.Status != UserStatus.Archived)
            .OrderBy(u => u.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        if (children.Count == 0) return Array.Empty<ChildSummary>();

        var childIds = children.Select(c => c.Id).ToList();
        var profiles = await _db.StudentProfiles.IgnoreQueryFilters()
            .Where(p => p.UserId != null && childIds.Contains(p.UserId!.Value))
            .ToListAsync(ct).ConfigureAwait(false);
        var profileByUserId = profiles
            .GroupBy(p => p.UserId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        return children.Select(u =>
        {
            profileByUserId.TryGetValue(u.Id, out var p);
            return new ChildSummary
            {
                UserId = u.Id.ToString("D"),
                Username = u.Username ?? string.Empty,
                FullName = u.FullName,
                FullNameEn = u.FullNameEn,
                Grade = ParseGrade(p?.Grade),
                Gender = p?.Gender ?? string.Empty,
                Status = u.Status.ToString().ToLowerInvariant(),
                LastLoginAt = u.LastLoginAt,
                CreatedAt = u.CreatedAt,
            };
        }).ToList();
    }

    public async Task<ChildDetail?> GetChildAsync(Guid parentUserId, Guid childUserId, CancellationToken ct = default)
    {
        var child = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == childUserId
                && u.ManagedByUserId == parentUserId
                && u.AccountType == AccountType.Managed
                && u.Status != UserStatus.Archived, ct).ConfigureAwait(false);
        if (child is null) return null;
        var profile = await _db.StudentProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == childUserId, ct).ConfigureAwait(false);
        return BuildChildDetail(child, profile);
    }

    public async Task<ChildOperationResult<ChildDetail>> UpdateChildAsync(UpdateChildCommand cmd, CancellationToken ct = default)
    {
        var child = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.ChildUserId
                && u.ManagedByUserId == cmd.ParentUserId
                && u.AccountType == AccountType.Managed, ct).ConfigureAwait(false);
        if (child is null)
        {
            return ChildOperationResult<ChildDetail>.Fail(404, "child_not_found", "الطفل غير موجود.");
        }
        if (child.Status == UserStatus.Archived)
        {
            return ChildOperationResult<ChildDetail>.Fail(409, "child_archived", "الحساب محذوف.");
        }

        if (!string.IsNullOrWhiteSpace(cmd.FullName))
        {
            child.FullName = cmd.FullName.Trim();
        }
        if (cmd.FullNameEn is not null)
        {
            child.FullNameEn = string.IsNullOrWhiteSpace(cmd.FullNameEn) ? null : cmd.FullNameEn.Trim();
        }
        child.UpdatedAt = DateTime.UtcNow;

        // Patch-style update of the StudentProfile; only persist fields
        // the caller actually supplied. Create a row on demand for legacy
        // children created before StudentProfile persistence existed.
        var profile = await _db.StudentProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == cmd.ChildUserId, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        if (profile is null)
        {
            profile = new StudentProfile
            {
                Id = Guid.NewGuid(),
                TenantId = child.TenantId,
                UserId = child.Id,
                DisplayName = child.FullName,
                CurriculumType = "MOE-EG",
                Grade = cmd.Grade.HasValue
                    ? cmd.Grade.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty,
                PreferredLanguage = child.Locale,
                PlanTier = "free",
                SubjectsEnrolled = "[]",
                ConsentState = "pending",
                Birthday = (cmd.Birthday.HasValue && cmd.Birthday.Value != default)
                    ? DateOnly.FromDateTime(cmd.Birthday.Value)
                    : null,
                Gender = string.IsNullOrWhiteSpace(cmd.Gender) ? null : cmd.Gender,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.StudentProfiles.Add(profile);
        }
        else
        {
            if (cmd.Grade.HasValue)
                profile.Grade = cmd.Grade.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(cmd.Gender))
                profile.Gender = cmd.Gender;
            // Only touch birthday if caller passed a real value (not the
            // DateTime.MinValue sentinel produced by legacy bogus rows).
            if (cmd.Birthday.HasValue && cmd.Birthday.Value != default)
                profile.Birthday = DateOnly.FromDateTime(cmd.Birthday.Value);
            if (!string.IsNullOrWhiteSpace(cmd.FullName))
                profile.DisplayName = child.FullName;
            profile.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Register.ToString(),
            ActorId = cmd.ParentUserId.ToString("D"),
            TenantId = cmd.ParentTenantId.ToString("D"),
            Action = "child_updated",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
            Reason = BuildChildMetadata(cmd.Grade, cmd.Gender, cmd.Birthday),
        });

        return ChildOperationResult<ChildDetail>.Ok(BuildChildDetail(child, profile), "تم تحديث بيانات الطفل.");
    }

    public async Task<ChildOperationResult<ChildCredentialsOnce>> RegenerateChildPasswordAsync(RegenerateChildPasswordCommand cmd, CancellationToken ct = default)
    {
        var child = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.ChildUserId
                && u.ManagedByUserId == cmd.ParentUserId
                && u.AccountType == AccountType.Managed, ct).ConfigureAwait(false);
        if (child is null)
        {
            return ChildOperationResult<ChildCredentialsOnce>.Fail(404, "child_not_found", "الطفل غير موجود.");
        }
        if (child.Status == UserStatus.Archived)
        {
            return ChildOperationResult<ChildCredentialsOnce>.Fail(409, "child_archived", "الحساب محذوف.");
        }

        var plaintext = string.IsNullOrEmpty(cmd.CustomPassword)
            ? _passwordGenerator.Generate(cmd.PasswordLocale)
            : cmd.CustomPassword;

        child.CompletePasswordReset(_passwords.Hash(plaintext));

        // Revoke every live refresh token for this child so the old
        // password can never be used again even with a stale refresh.
        var liveTokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == child.Id && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in liveTokens)
        {
            t.MarkFamilyRevoked();
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.PasswordChange.ToString(),
            ActorId = cmd.ParentUserId.ToString("D"),
            TenantId = cmd.ParentTenantId.ToString("D"),
            Action = "child_password_regenerated",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return ChildOperationResult<ChildCredentialsOnce>.Ok(new ChildCredentialsOnce
        {
            UserId = child.Id.ToString("D"),
            Username = child.Username ?? string.Empty,
            GeneratedPassword = plaintext,
            FullName = child.FullName,
            Grade = 0,
            TenantId = child.TenantId.ToString("D"),
            CreatedAt = child.CreatedAt,
        }, "تم إنشاء كلمة مرور جديدة.");
    }

    public async Task<ChildOperationResult<object>> DeleteChildAsync(DeleteChildCommand cmd, CancellationToken ct = default)
    {
        var child = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.ChildUserId
                && u.ManagedByUserId == cmd.ParentUserId
                && u.AccountType == AccountType.Managed, ct).ConfigureAwait(false);
        if (child is null)
        {
            return ChildOperationResult<object>.Fail(404, "child_not_found", "الطفل غير موجود.");
        }
        if (child.Status == UserStatus.Archived)
        {
            return ChildOperationResult<object>.Ok(new { deleted = true }, "الحساب محذوف.");
        }

        child.Archive();

        var liveTokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == child.Id && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in liveTokens)
        {
            t.MarkFamilyRevoked();
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.Register.ToString(),
            ActorId = cmd.ParentUserId.ToString("D"),
            TenantId = cmd.ParentTenantId.ToString("D"),
            Action = "child_deleted",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return ChildOperationResult<object>.Ok(new { deleted = true }, "تم حذف الحساب.");
    }

    // ── US5: Parent oversight ─────────────────────────────────────────────

    public async Task<ChildOperationResult<object>> SuspendChildAsync(SuspendChildCommand cmd, CancellationToken ct = default)
    {
        var child = await FindOwnedChildAsync(cmd.ParentUserId, cmd.ChildUserId, ct).ConfigureAwait(false);
        if (child is null)
            return ChildOperationResult<object>.Fail(404, "child_not_found", "الطفل غير موجود.");
        if (child.Status == UserStatus.Archived)
            return ChildOperationResult<object>.Fail(409, "child_archived", "الحساب محذوف.");
        if (child.Status == UserStatus.Suspended)
            return ChildOperationResult<object>.Ok(new { suspended = true }, "الحساب معلّق بالفعل.");

        child.Suspend();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.AccountSuspended.ToString(),
            ActorId = cmd.ParentUserId.ToString("D"),
            TenantId = cmd.ParentTenantId.ToString("D"),
            Action = "child_suspended",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return ChildOperationResult<object>.Ok(new { suspended = true }, "تم تعليق الحساب.");
    }

    public async Task<ChildOperationResult<object>> UnsuspendChildAsync(UnsuspendChildCommand cmd, CancellationToken ct = default)
    {
        var child = await FindOwnedChildAsync(cmd.ParentUserId, cmd.ChildUserId, ct).ConfigureAwait(false);
        if (child is null)
            return ChildOperationResult<object>.Fail(404, "child_not_found", "الطفل غير موجود.");
        if (child.Status == UserStatus.Archived)
            return ChildOperationResult<object>.Fail(409, "child_archived", "الحساب محذوف.");
        if (child.Status != UserStatus.Suspended)
            return ChildOperationResult<object>.Ok(new { suspended = false }, "الحساب نشط بالفعل.");

        child.Unsuspend();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.AccountUnsuspended.ToString(),
            ActorId = cmd.ParentUserId.ToString("D"),
            TenantId = cmd.ParentTenantId.ToString("D"),
            Action = "child_unsuspended",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return ChildOperationResult<object>.Ok(new { suspended = false }, "تم إعادة تفعيل الحساب.");
    }

    public async Task<IReadOnlyList<ChildSessionSummary>> ListChildSessionsAsync(
        Guid parentUserId, Guid childUserId, CancellationToken ct = default)
    {
        // Verify ownership before exposing session data.
        var child = await FindOwnedChildAsync(parentUserId, childUserId, ct).ConfigureAwait(false);
        if (child is null) return Array.Empty<ChildSessionSummary>();

        var sessions = await _db.IdentityUserSessions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == childUserId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return sessions.Select(s => new ChildSessionSummary
        {
            SessionId = s.Id.ToString("D"),
            DeviceName = s.DeviceName,
            DeviceType = s.DeviceType.ToString().ToLowerInvariant(),
            IpAddress = s.IpAddress,
            UserAgent = s.UserAgent,
            CreatedAt = s.CreatedAt,
            LastSeenAt = s.LastSeenAt,
        }).ToList();
    }

    public async Task<ChildOperationResult<object>> RevokeChildSessionAsync(
        RevokeChildSessionCommand cmd, CancellationToken ct = default)
    {
        var child = await FindOwnedChildAsync(cmd.ParentUserId, cmd.ChildUserId, ct).ConfigureAwait(false);
        if (child is null)
            return ChildOperationResult<object>.Fail(404, "child_not_found", "الطفل غير موجود.");

        var session = await _db.IdentityUserSessions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == cmd.SessionId && s.UserId == cmd.ChildUserId, ct)
            .ConfigureAwait(false);
        if (session is null || session.RevokedAt is not null)
            return ChildOperationResult<object>.Fail(404, "session_not_found", "الجلسة غير موجودة.");

        session.Revoke();

        // Invalidate any refresh tokens for this session.
        var tokens = await _db.IdentityRefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.SessionId == cmd.SessionId && t.RevokedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var t in tokens) t.MarkFamilyRevoked();

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.SessionRevoked.ToString(),
            ActorId = cmd.ParentUserId.ToString("D"),
            TenantId = cmd.ParentTenantId.ToString("D"),
            Action = "child_session_revoked",
            TargetType = "UserSession",
            TargetId = cmd.SessionId.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return ChildOperationResult<object>.Ok(new { revoked = true }, "تم إنهاء الجلسة.");
    }

    public async Task<IReadOnlyList<ChildLoginHistoryItem>> GetChildLoginHistoryAsync(
        Guid parentUserId, Guid childUserId, int limit, CancellationToken ct = default)
    {
        var child = await FindOwnedChildAsync(parentUserId, childUserId, ct).ConfigureAwait(false);
        if (child is null) return Array.Empty<ChildLoginHistoryItem>();

        var attempts = await _db.IdentityLoginAttempts
            .IgnoreQueryFilters()
            .Where(la => la.UserId == childUserId)
            .OrderByDescending(la => la.AttemptedAt)
            .Take(limit > 0 ? limit : 50)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return attempts.Select(la => new ChildLoginHistoryItem
        {
            Id = la.Id.ToString("D"),
            IpAddress = MaskIp(la.IpAddress),
            UserAgent = SummariseUa(la.UserAgent),
            Outcome = la.Outcome.ToString().ToLowerInvariant(),
            FailureReason = la.FailureReason,
            AttemptedAt = la.AttemptedAt,
        }).ToList();
    }

    private async Task<User?> FindOwnedChildAsync(Guid parentUserId, Guid childUserId, CancellationToken ct)
        => await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == childUserId
                && u.ManagedByUserId == parentUserId
                && u.AccountType == AccountType.Managed, ct)
            .ConfigureAwait(false);

    private static ChildDetail BuildChildDetail(User child, StudentProfile? profile)
        => new()
        {
            UserId = child.Id.ToString("D"),
            Username = child.Username ?? string.Empty,
            FullName = child.FullName,
            FullNameEn = child.FullNameEn,
            Grade = ParseGrade(profile?.Grade),
            Gender = profile?.Gender ?? string.Empty,
            Birthday = profile?.Birthday is { } d ? d.ToDateTime(TimeOnly.MinValue) : DateTime.MinValue,
            Status = child.Status.ToString().ToLowerInvariant(),
            Locale = child.Locale,
            LastLoginAt = child.LastLoginAt,
            TenantId = child.TenantId.ToString("D"),
            ManagedByUserId = child.ManagedByUserId?.ToString("D") ?? string.Empty,
            CreatedAt = child.CreatedAt,
            UpdatedAt = child.UpdatedAt,
        };

    private static int ParseGrade(string? grade)
    {
        if (string.IsNullOrWhiteSpace(grade)) return 0;
        return int.TryParse(grade, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var g)
            ? g
            : 0;
    }

    private static string BuildChildMetadata(int? grade, string? gender, DateTime? birthday)
    {
        var parts = new List<string>();
        if (grade.HasValue) parts.Add($"\"grade\":{grade.Value}");
        if (!string.IsNullOrWhiteSpace(gender)) parts.Add($"\"gender\":\"{gender}\"");
        if (birthday.HasValue) parts.Add($"\"birthday\":\"{birthday.Value:yyyy-MM-dd}\"");
        return "{" + string.Join(",", parts) + "}";
    }

    private static string MaskIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "0.0.0.x";
        var parts = ip.Split('.');
        if (parts.Length == 4)
        {
            parts[3] = "x";
            return string.Join('.', parts);
        }
        return ip;
    }

    private static string? SummariseUa(string? ua)
        => ua is null ? null : (ua.Length > 80 ? ua[..80] : ua);
}
