using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Application.Audit;
using Muallimi.Application.Common;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Credentials;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Application.Identity.Services;
using Muallimi.Application.Identity.Validators;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Domain.Parents;
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

    /// <summary>Phase 9 Phase 3: parent resets the 4-digit PIN for an 8–12 child.</summary>
    Task<ChildOperationResult<object>> ResetChildPinAsync(ResetChildPinCommand cmd, CancellationToken ct = default);

    /// <summary>Phase 9 Phase 3: parent adds a PIN on a child's 8th-birthday tier transition.</summary>
    Task<ChildOperationResult<object>> AddChildPinAsync(AddChildPinCommand cmd, CancellationToken ct = default);

    /// <summary>Phase 9 Phase 3: parent upgrades an 8–12 PIN child to the 13+ password tier.</summary>
    Task<ChildOperationResult<object>> UpgradeChildToPasswordAsync(UpgradeChildToPasswordCommand cmd, CancellationToken ct = default);

    /// <summary>Add-child redesign Phase 6: parent-only unlock for a Locked child (post-PIN-failure).</summary>
    Task<ChildOperationResult<object>> UnlockChildAsync(UnlockChildCommand cmd, CancellationToken ct = default);

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

/// <summary>
/// Result row for the duplicate-child match search. Surfaced inside
/// <see cref="UserManagementService.CreateChildAsync"/> as the 409
/// `duplicate_child` envelope so the parent can open the existing child
/// or confirm "twins — add anyway".
/// </summary>
internal sealed record DuplicateChildMatch(Guid ChildId, string FullName);

public sealed class UserManagementService : IUserManagementService
{
    private readonly MuallimiDbContext _db;
    private readonly IPasswordService _passwords;
    private readonly IUsernameGenerator _usernameGenerator;
    private readonly IChildPasswordGenerator _passwordGenerator;
    private readonly AuditEventEmitter _audit;
    private readonly IIdentityNotificationSender _notifications;
    private readonly ILogger<UserManagementService> _logger;
    private readonly IWeakPinBlocklist _weakPinBlocklist;

    private readonly IManagerReAuthService _reauth;
    private readonly ICredentialAuditWriter _credentialAudit;
    private readonly IPasswordStrengthValidator _passwordStrength;

    public UserManagementService(
        MuallimiDbContext db,
        IPasswordService passwords,
        IUsernameGenerator usernameGenerator,
        IChildPasswordGenerator passwordGenerator,
        AuditEventEmitter audit,
        IIdentityNotificationSender notifications,
        ILogger<UserManagementService> logger,
        IWeakPinBlocklist weakPinBlocklist,
        IManagerReAuthService reauth,
        ICredentialAuditWriter credentialAudit,
        IPasswordStrengthValidator passwordStrength)
    {
        _db = db;
        _passwords = passwords;
        _usernameGenerator = usernameGenerator;
        _passwordGenerator = passwordGenerator;
        _audit = audit;
        _notifications = notifications;
        _logger = logger;
        _weakPinBlocklist = weakPinBlocklist;
        _reauth = reauth;
        _credentialAudit = credentialAudit;
        _passwordStrength = passwordStrength;
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

        // Phase 9 follow-up — duplicate-child detection. Match key is the
        // normalized full-name + (birth year, birth month) scoped to THIS
        // parent's children only (not tenant-wide; in the B2C tenant
        // multiple unrelated parents legitimately have kids with the
        // same name + birthday). Twins are handled via the parent
        // re-submitting with `ConfirmDuplicate=true` after the dialog.
        //
        // We normalize Arabic in-process because the DB has no
        // collation function for the alif/ya/ta-marbuta folding we need.
        // Per parent there are typically 1-5 siblings, so the in-memory
        // scan is cheap.
        DuplicateChildMatch? duplicate = null;
        var normalizedNewName = NameNormalization.NormalizeArabic(cmd.FullName);
        if (!string.IsNullOrEmpty(normalizedNewName))
        {
            var siblings = await (
                from u in _db.IdentityUsers.IgnoreQueryFilters()
                join p in _db.StudentProfiles.IgnoreQueryFilters() on u.Id equals p.UserId into joined
                from p in joined.DefaultIfEmpty()
                where u.ManagedByUserId == cmd.ParentUserId
                   && u.AccountType == AccountType.Managed
                   && u.Status != UserStatus.Archived
                select new { u.Id, u.FullName, Birthday = p == null ? (DateOnly?)null : p.Birthday }
            ).ToListAsync(ct).ConfigureAwait(false);

            duplicate = siblings
                .Where(s =>
                    NameNormalization.NormalizeArabic(s.FullName) == normalizedNewName
                    && s.Birthday.HasValue
                    && s.Birthday.Value.Year == cmd.BirthYear
                    && s.Birthday.Value.Month == cmd.BirthMonth)
                .Select(s => new DuplicateChildMatch(s.Id, s.FullName))
                .FirstOrDefault();

            if (duplicate is not null && !cmd.ConfirmDuplicate)
            {
                // First attempt — surface the conflict so the parent can
                // either open the existing child or confirm "this is a
                // different child (twins)". The existing-child id is
                // packed into ApiResponseError.Field; the human-readable
                // name goes in Message. The frontend looks for code
                // `duplicate_child` to trigger the picker dialog.
                return new ChildOperationResult<ChildCredentialsOnce>(
                    Success: false,
                    HttpStatus: 409,
                    Message: $"الطفل {duplicate.FullName} موجود مسبقًا.",
                    Errors: new[]
                    {
                        new ApiResponseError
                        {
                            Code = "duplicate_child",
                            Field = duplicate.ChildId.ToString("D"),
                            Message = duplicate.FullName,
                        },
                    },
                    ErrorCode: "duplicate_child");
            }

            if (duplicate is not null && cmd.ConfirmDuplicate)
            {
                // Parent explicitly re-submitted "Add anyway" — log the
                // override BEFORE we try to create so we have a trail
                // even if creation later fails. The audit row will
                // outlive the request via AuditEventEmitter's outbox.
                _audit.Emit(new AuditEvent
                {
                    EventCategory = AuthEventCategory.Register.ToString(),
                    ActorId = parent.Id.ToString("D"),
                    TenantId = parent.TenantId.ToString("D"),
                    Action = "child_duplicate_override",
                    TargetType = "User",
                    TargetId = duplicate.ChildId.ToString("D"),
                    Outcome = "succeeded",
                    CorrelationId = cmd.CorrelationId,
                    Reason = $"Parent confirmed duplicate add for name='{cmd.FullName}' birth={cmd.BirthYear}/{cmd.BirthMonth} (existing child {duplicate.ChildId:D}).",
                });
            }
        }

        string username;
        try
        {
            username = await _usernameGenerator.GenerateAsync(
                cmd.FullName,
                cmd.BirthYear,
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

        // Resolve credentials per login method. Only username_password
        // ever returns a plaintext password to surface in the success
        // screen (and only when parent did NOT supply a custom one).
        string? passwordHash = null;
        string? pinHash = null;
        string? plaintextPasswordToReturn = null;
        switch (cmd.LoginMethod)
        {
            case "profile_switch_only":
                // No credential at all. Under-8 children are accessed
                // exclusively via parent profile-switch.
                break;
            case "pin":
                if (_weakPinBlocklist.IsWeak(cmd.Pin!, cmd.BirthYear))
                {
                    return ChildOperationResult<ChildCredentialsOnce>.Fail(422, "pin_too_weak", "رمز PIN ضعيف. اختر رمزًا أصعب.");
                }
                pinHash = _passwords.Hash(cmd.Pin!);
                break;
            case "username_password":
                if (string.IsNullOrEmpty(cmd.CustomPassword))
                {
                    // English wordlist — ASCII-only so the password is safe
                    // through every JSON / display / clipboard path. Arabic
                    // (used to be the default here) tripped up the success
                    // screen render in some environments.
                    plaintextPasswordToReturn = _passwordGenerator.Generate("en");
                    passwordHash = _passwords.Hash(plaintextPasswordToReturn);
                }
                else
                {
                    plaintextPasswordToReturn = cmd.CustomPassword;
                    passwordHash = _passwords.Hash(cmd.CustomPassword);
                }
                break;
        }

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
            Locale = parent.Locale,
            Status = UserStatus.Active,
            PasswordHash = passwordHash,
            PasswordChangedAt = passwordHash is null ? null : DateTime.UtcNow,
            LoginMethod = cmd.LoginMethod,
            PinHash = pinHash,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = parent.Id,
        };
        child.AssertAccountTypeInvariants();

        var birthday = new DateOnly(cmd.BirthYear, cmd.BirthMonth, 1);
        var metadata = BuildChildMetadata(cmd.Grade, cmd.Gender, birthday);

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

        var now = DateTime.UtcNow;
        var profile = new StudentProfile
        {
            Id = Guid.NewGuid(),
            TenantId = parent.TenantId,
            UserId = child.Id,
            DisplayName = child.FullName,
            CurriculumType = cmd.CurriculumType,
            Grade = cmd.Grade.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PreferredLanguage = child.Locale,
            PlanTier = "free",
            SubjectsEnrolled = "[]",
            ConsentState = "pending",
            Birthday = birthday,
            Gender = string.IsNullOrWhiteSpace(cmd.Gender) ? null : cmd.Gender,
            AvatarReference = cmd.AvatarEmoji,
            AvatarBgColor = cmd.AvatarBgColor,
            SchoolName = string.IsNullOrWhiteSpace(cmd.SchoolName) ? null : cmd.SchoolName.Trim(),
            PrefLevel = cmd.PrefLevel,
            PrefStyles = cmd.PrefStyles,
            PrefGoal = cmd.PrefGoal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.StudentProfiles.Add(profile);

        // Add-child redesign decision #10: persist explicit parental consent.
        var consent = new ParentalConsent
        {
            Id = Guid.NewGuid(),
            TenantId = parent.TenantId,
            ParentUserId = parent.Id,
            ChildUserId = child.Id,
            ConsentedAt = now,
            IpAddress = string.IsNullOrWhiteSpace(cmd.IpAddress) ? null : cmd.IpAddress,
            IsLegacyAssumed = false,
            CreatedAt = now,
        };
        _db.IdentityParentalConsents.Add(consent);

        // Phase 4 ↔ Phase 9 bridge: every parent surface (notifications inbox,
        // dashboard tiles, weekly report) filters by ChildLink. Without this
        // row, child-scoped notifications fan out (the email arrives) but
        // never surface in the parent's inbox or dashboard. Look up the
        // ParentProfile by user id and create a guardian-role link covering
        // an open-ended effective window.
        var parentProfileId = await _db.ParentProfiles.IgnoreQueryFilters()
            .Where(p => p.UserId == parent.Id)
            .Select(p => (Guid?)p.ParentProfileId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (parentProfileId is { } pid)
        {
            _db.ChildLinks.Add(new ChildLink
            {
                ChildLinkId = Guid.NewGuid(),
                TenantId = parent.TenantId,
                ParentProfileId = pid,
                StudentId = child.Id,
                Role = "guardian",
                EffectiveStart = now.Date,
                EffectiveEnd = null,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            _logger.LogWarning(
                "ParentProfile missing for parent {ParentId} when creating child {ChildId} — inbox visibility deferred until Phase 4 backfill",
                parent.Id, child.Id);
        }

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
                plaintextPasswordToReturn ?? string.Empty,
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
            LoginMethod = cmd.LoginMethod,
            GeneratedPassword = plaintextPasswordToReturn,
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
                Grade = ParseGrade(p?.Grade),
                Gender = p?.Gender,
                AvatarEmoji = p?.AvatarReference,
                AvatarBgColor = p?.AvatarBgColor,
                Birthday = p?.Birthday,
                LoginMethod = u.LoginMethod,
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

        // Add-child redesign decision #5: parent-driven username change.
        // Re-check global uniqueness, then revoke ALL child sessions
        // (access + refresh) so any cached login on another device dies.
        var usernameChanged = false;
        if (!string.IsNullOrWhiteSpace(cmd.Username))
        {
            var nu = cmd.Username.Trim();
            var nuLower = nu.ToLowerInvariant();
            if (child.NormalizedUsername != nuLower)
            {
                var clash = await _db.IdentityUsers.IgnoreQueryFilters()
                    .AnyAsync(u => u.NormalizedUsername == nuLower && u.Id != child.Id, ct).ConfigureAwait(false);
                if (clash)
                {
                    return ChildOperationResult<ChildDetail>.Fail(409, "username_unavailable", "اسم المستخدم غير متاح.");
                }
                child.Username = nu;
                child.NormalizedUsername = nuLower;
                usernameChanged = true;
            }
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
                CurriculumType = string.IsNullOrWhiteSpace(cmd.CurriculumType) ? "MOE-EG" : cmd.CurriculumType,
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
                AvatarReference = string.IsNullOrWhiteSpace(cmd.AvatarEmoji) ? null : cmd.AvatarEmoji,
                AvatarBgColor = string.IsNullOrWhiteSpace(cmd.AvatarBgColor) ? null : cmd.AvatarBgColor,
                SchoolName = string.IsNullOrWhiteSpace(cmd.SchoolName) ? null : cmd.SchoolName.Trim(),
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
            // EditChildDrawer profile-only fields: emoji, color,
            // curriculum, school. Null = "leave alone"; whitespace
            // schoolName clears it (parent removed it from the form).
            if (!string.IsNullOrWhiteSpace(cmd.AvatarEmoji))
                profile.AvatarReference = cmd.AvatarEmoji;
            if (!string.IsNullOrWhiteSpace(cmd.AvatarBgColor))
                profile.AvatarBgColor = cmd.AvatarBgColor;
            if (!string.IsNullOrWhiteSpace(cmd.CurriculumType))
                profile.CurriculumType = cmd.CurriculumType;
            if (cmd.SchoolName is not null)
                profile.SchoolName = string.IsNullOrWhiteSpace(cmd.SchoolName) ? null : cmd.SchoolName.Trim();
            profile.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (usernameChanged)
        {
            // Revoke ALL child sessions (access + refresh).
            var sessions = await _db.IdentityUserSessions.IgnoreQueryFilters()
                .Where(s => s.UserId == child.Id && s.RevokedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var s in sessions) s.Revoke();

            var sessionIds = sessions.Select(s => s.Id).ToList();
            if (sessionIds.Count > 0)
            {
                var refresh = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
                    .Where(t => sessionIds.Contains(t.SessionId) && t.RevokedAt == null)
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var t in refresh) t.MarkFamilyRevoked();
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _audit.Emit(new AuditEvent
            {
                EventCategory = AuthEventCategory.SessionRevoked.ToString(),
                ActorId = cmd.ParentUserId.ToString("D"),
                TenantId = cmd.ParentTenantId.ToString("D"),
                Action = "child_username_changed",
                TargetType = "User",
                TargetId = child.Id.ToString("D"),
                Outcome = "succeeded",
                CorrelationId = cmd.CorrelationId,
            });
        }

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
            Reason = BuildChildMetadata(
                cmd.Grade,
                cmd.Gender,
                cmd.Birthday.HasValue && cmd.Birthday.Value != default
                    ? DateOnly.FromDateTime(cmd.Birthday.Value)
                    : (DateOnly?)null),
        });

        return ChildOperationResult<ChildDetail>.Ok(BuildChildDetail(child, profile), "تم تحديث بيانات الطفل.");
    }

    public async Task<ChildOperationResult<ChildCredentialsOnce>> RegenerateChildPasswordAsync(RegenerateChildPasswordCommand cmd, CancellationToken ct = default)
    {
        // Phase 9 Phase 3 hardening: re-auth gate, tier guard, credential
        // audit, post-reset notice, optimistic concurrency. The plaintext
        // generation + once-only payload remain — that's the existing US2
        // contract and unchanged.
        if (!await _reauth.HasRecentReAuthAsync(cmd.ParentUserId, ct).ConfigureAwait(false))
            return ChildOperationResult<ChildCredentialsOnce>.Fail(401, "reauth_required", "يرجى التحقق من هويتك أولًا.");

        var child = await LoadOwnedChildAsync(cmd.ParentUserId, cmd.ChildUserId, ct).ConfigureAwait(false);
        if (child is null)
            return ChildOperationResult<ChildCredentialsOnce>.Fail(404, "child_not_found", "الطفل غير موجود.");
        if (child.Status == UserStatus.Archived)
            return ChildOperationResult<ChildCredentialsOnce>.Fail(409, "child_archived", "الحساب محذوف.");
        if (child.LoginMethod != LoginMethods.UsernamePassword)
            return ChildOperationResult<ChildCredentialsOnce>.Fail(409, "tier_mismatch", "لا يمكن تنفيذ هذه العملية على هذا الحساب.");

        var plaintext = string.IsNullOrEmpty(cmd.CustomPassword)
            ? _passwordGenerator.Generate(cmd.PasswordLocale)
            : cmd.CustomPassword;

        // Strength check on parent-supplied custom passwords (zxcvbn ≥ 3).
        // Generated passwords are guaranteed strong by ChildPasswordGenerator.
        if (!string.IsNullOrEmpty(cmd.CustomPassword))
        {
            var inputs = new[] { child.Username ?? string.Empty, child.FullName ?? string.Empty };
            var strength = _passwordStrength.Evaluate(cmd.CustomPassword, inputs);
            if (!strength.IsAcceptable)
            {
                return ChildOperationResult<ChildCredentialsOnce>.Fail(422, "weak_password",
                    string.Equals(child.Locale, "en", StringComparison.OrdinalIgnoreCase) ? strength.FeedbackEn : strength.FeedbackAr);
            }
        }

        child.CompletePasswordReset(_passwords.Hash(plaintext));
        child.MarkPendingParentResetNotice();

        await RevokeChildRefreshTokensAsync(child.Id, ct).ConfigureAwait(false);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChildOperationResult<ChildCredentialsOnce>.Fail(409, "concurrency_conflict",
                "تم تغيير كلمة المرور من جلسة أخرى — أعد المحاولة.");
        }

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
        await EmitCredentialAuditAsync(
            CredentialAuditEventKind.ParentResetChildPassword,
            cmd.ParentUserId, child, cmd.IpAddress, cmd.UserAgent, cmd.CorrelationId, ct).ConfigureAwait(false);

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

    // ── Phase 9 Phase 3: parent reset / add / upgrade ─────────────────

    public Task<ChildOperationResult<object>> ResetChildPinAsync(ResetChildPinCommand cmd, CancellationToken ct = default)
        => ExecuteCredentialActionAsync(
            cmd.ParentUserId, cmd.ChildUserId, cmd.IpAddress, cmd.UserAgent, cmd.CorrelationId,
            requiredTier: LoginMethods.Pin,
            validateAsync: (child, cct) => ValidatePinAsync(child, cmd.NewPin, cct),
            mutate: child => child.SetPin(_passwords.Hash(cmd.NewPin)),
            auditKind: CredentialAuditEventKind.ParentResetChildPin,
            stampPendingNotice: true,
            successMessage: "تم إعادة تعيين رمز PIN.",
            ct);

    public Task<ChildOperationResult<object>> AddChildPinAsync(AddChildPinCommand cmd, CancellationToken ct = default)
        => ExecuteCredentialActionAsync(
            cmd.ParentUserId, cmd.ChildUserId, cmd.IpAddress, cmd.UserAgent, cmd.CorrelationId,
            requiredTier: LoginMethods.ProfileSwitchOnly,
            validateAsync: (child, cct) => ValidatePinAsync(child, cmd.NewPin, cct),
            mutate: child => child.AddPinForUnderEight(_passwords.Hash(cmd.NewPin)),
            auditKind: CredentialAuditEventKind.ParentAddedChildPin,
            stampPendingNotice: false,  // child has no prior session to "notice"
            successMessage: "تم إضافة رقم PIN.",
            ct);

    public Task<ChildOperationResult<object>> UpgradeChildToPasswordAsync(UpgradeChildToPasswordCommand cmd, CancellationToken ct = default)
        => ExecuteCredentialActionAsync(
            cmd.ParentUserId, cmd.ChildUserId, cmd.IpAddress, cmd.UserAgent, cmd.CorrelationId,
            requiredTier: LoginMethods.Pin,
            validateAsync: (child, cct) => Task.FromResult(ValidatePasswordStrength(child, cmd.NewPassword)),
            mutate: child => child.UpgradePinToPassword(_passwords.Hash(cmd.NewPassword)),
            auditKind: CredentialAuditEventKind.ParentUpgradedChildToPassword,
            stampPendingNotice: true,
            successMessage: "تم ترقية الحساب إلى كلمة مرور.",
            ct);

    // ── Shared helpers (single-source the credential pipeline) ─────────

    private async Task<User?> LoadOwnedChildAsync(Guid parentUserId, Guid childUserId, CancellationToken ct)
        => await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                u => u.Id == childUserId
                  && u.ManagedByUserId == parentUserId
                  && u.AccountType == AccountType.Managed,
                ct).ConfigureAwait(false);

    private async Task RevokeChildRefreshTokensAsync(Guid childId, CancellationToken ct)
    {
        var liveTokens = await _db.IdentityRefreshTokens.IgnoreQueryFilters()
            .Where(t => t.UserId == childId && t.RevokedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in liveTokens) t.MarkFamilyRevoked();
    }

    private async Task<ChildOperationResult<object>> ExecuteCredentialActionAsync(
        Guid parentUserId,
        Guid childUserId,
        string ipAddress,
        string? userAgent,
        string correlationId,
        string requiredTier,
        Func<User, CancellationToken, Task<ChildOperationResult<object>?>> validateAsync,
        Action<User> mutate,
        CredentialAuditEventKind auditKind,
        bool stampPendingNotice,
        string successMessage,
        CancellationToken ct)
    {
        // 1) Re-auth recency gate.
        if (!await _reauth.HasRecentReAuthAsync(parentUserId, ct).ConfigureAwait(false))
            return ChildOperationResult<object>.Fail(401, "reauth_required", "يرجى التحقق من هويتك أولًا.");

        // 2) Load child + verify ownership.
        var child = await LoadOwnedChildAsync(parentUserId, childUserId, ct).ConfigureAwait(false);
        if (child is null || child.Status == UserStatus.Archived)
            return ChildOperationResult<object>.Fail(404, "child_not_found", "الحساب غير موجود.");

        // 3) Tier guard.
        if (!string.Equals(child.LoginMethod, requiredTier, StringComparison.Ordinal))
            return ChildOperationResult<object>.Fail(409, "tier_mismatch", "لا يمكن تنفيذ هذه العملية على هذا الحساب.");

        // 4) Action-specific validation.
        var validationFailure = await validateAsync(child, ct).ConfigureAwait(false);
        if (validationFailure is not null) return validationFailure;

        // 5) + 6) Apply mutation + post-reset notice.
        mutate(child);
        if (stampPendingNotice) child.MarkPendingParentResetNotice();
        await RevokeChildRefreshTokensAsync(child.Id, ct).ConfigureAwait(false);

        // 7) Save — DbUpdateConcurrencyException → HTTP 409.
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ChildOperationResult<object>.Fail(409, "concurrency_conflict",
                "تم تغيير كلمة المرور من جلسة أخرى — أعد المحاولة.");
        }

        // 8) Credential audit (DB-backed, PII-masked).
        await EmitCredentialAuditAsync(auditKind, parentUserId, child, ipAddress, userAgent, correlationId, ct).ConfigureAwait(false);

        return ChildOperationResult<object>.Ok(new { ok = true }, successMessage);
    }

    private async Task EmitCredentialAuditAsync(
        CredentialAuditEventKind kind,
        Guid actorParentId,
        User child,
        string ipAddress,
        string? userAgent,
        string correlationId,
        CancellationToken ct)
    {
        try
        {
            await _credentialAudit.WriteAsync(new CredentialAuditEvent
            {
                Kind = kind,
                TenantId = child.TenantId,
                ActorId = actorParentId,
                ActorType = CredentialAuditActorTypes.User,
                TargetUserId = child.Id,
                CorrelationId = correlationId,
                IpAddress = ipAddress,
                UserAgent = userAgent,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write credential audit (kind {Kind}, child {ChildId})", kind, child.Id);
        }
    }

    private ChildOperationResult<object>? ValidatePasswordStrength(User child, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            return ChildOperationResult<object>.Fail(422, "weak_password", "كلمة المرور مطلوبة.");
        var inputs = new[] { child.Username ?? string.Empty, child.FullName ?? string.Empty };
        var strength = _passwordStrength.Evaluate(newPassword, inputs);
        if (!strength.IsAcceptable)
        {
            return ChildOperationResult<object>.Fail(422, "weak_password",
                string.Equals(child.Locale, "en", StringComparison.OrdinalIgnoreCase) ? strength.FeedbackEn : strength.FeedbackAr);
        }
        return null;
    }

    private async Task<ChildOperationResult<object>?> ValidatePinAsync(User child, string newPin, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(newPin) || newPin.Length != 4 || !int.TryParse(newPin, out _))
            return ChildOperationResult<object>.Fail(422, "invalid_pin", "رمز PIN يجب أن يكون 4 أرقام.");
        var profile = await _db.StudentProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.UserId == child.Id, ct).ConfigureAwait(false);
        if (_weakPinBlocklist.IsWeak(newPin, profile?.Birthday?.Year))
            return ChildOperationResult<object>.Fail(422, "weak_pin", "هذا الرقم شائع جدًا — اختر رقمًا أصعب.");
        return null;
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

    public async Task<ChildOperationResult<object>> UnlockChildAsync(UnlockChildCommand cmd, CancellationToken ct = default)
    {
        // Verify the parent password — defense in depth on top of JWT auth.
        var parent = await _db.IdentityUsers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == cmd.ParentUserId, ct).ConfigureAwait(false);
        if (parent is null || string.IsNullOrEmpty(parent.PasswordHash))
        {
            return ChildOperationResult<object>.Fail(401, "invalid_parent_password", "كلمة مرور ولي الأمر غير صحيحة.");
        }
        if (!_passwords.VerifyWithDummyFallback(cmd.ParentPassword, parent.PasswordHash))
        {
            return ChildOperationResult<object>.Fail(401, "invalid_parent_password", "كلمة مرور ولي الأمر غير صحيحة.");
        }

        var child = await FindOwnedChildAsync(cmd.ParentUserId, cmd.ChildUserId, ct).ConfigureAwait(false);
        if (child is null)
            return ChildOperationResult<object>.Fail(404, "child_not_found", "الطفل غير موجود.");
        if (child.Status == UserStatus.Archived)
            return ChildOperationResult<object>.Fail(409, "child_archived", "الحساب محذوف.");

        if (child.Status != UserStatus.Locked)
        {
            return ChildOperationResult<object>.Ok(new { locked = false }, "الحساب غير مقفل.");
        }

        child.Status = UserStatus.Active;
        child.FailedLoginAttempts = 0;
        child.LockoutEnd = null;
        child.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _audit.Emit(new AuditEvent
        {
            EventCategory = AuthEventCategory.AccountUnsuspended.ToString(),
            ActorId = cmd.ParentUserId.ToString("D"),
            TenantId = cmd.ParentTenantId.ToString("D"),
            Action = "child_account_unlocked",
            TargetType = "User",
            TargetId = child.Id.ToString("D"),
            Outcome = "succeeded",
            CorrelationId = cmd.CorrelationId,
        });

        return ChildOperationResult<object>.Ok(new { locked = false }, "تم فك قفل الحساب.");
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
            Grade = ParseGrade(profile?.Grade),
            Gender = profile?.Gender,
            BirthYear = profile?.Birthday?.Year,
            BirthMonth = profile?.Birthday?.Month,
            CurriculumType = profile?.CurriculumType,
            SchoolName = profile?.SchoolName,
            AvatarEmoji = profile?.AvatarReference,
            AvatarBgColor = profile?.AvatarBgColor,
            PrefLevel = profile?.PrefLevel,
            PrefStyles = profile?.PrefStyles,
            PrefGoal = profile?.PrefGoal,
            LoginMethod = child.LoginMethod,
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

    private static string BuildChildMetadata(int? grade, string? gender, DateOnly? birthday)
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
