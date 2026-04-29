using System;
using System.Collections.Generic;
using Muallimi.Application.Identity.Dtos;

namespace Muallimi.Application.Identity.Commands;

/// <summary>
/// T090 — Commands for the parent-children surface (US2). Every command
/// carries the acting parent's user + tenant ids (populated by the
/// endpoint from the JWT claims) plus the standard request context
/// (IP, user-agent, correlation id) the audit pipeline needs.
/// </summary>
public sealed record CreateChildCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    string FullName,
    int Grade,
    string? Gender,
    int BirthYear,
    int BirthMonth,
    string CurriculumType,
    string? SchoolName,
    // Avatar
    string AvatarEmoji,
    string AvatarBgColor,
    // Learning preferences (all optional — step may be skipped)
    string? PrefLevel,
    string? PrefStyles,
    string? PrefGoal,
    // Login method
    string LoginMethod,          // "profile_switch_only" | "pin" | "username_password"
    string? Pin,                 // plain 4-digit, hashed by service; only for "pin"
    string? PreferredUsername,   // only for "username_password"
    string? CustomPassword,      // only for "username_password"
    bool ParentalConsentAcknowledged, // explicit checkbox in step 6
    string IpAddress,
    string? UserAgent,
    string CorrelationId,
    /// <summary>
    /// Phase 9 follow-up — duplicate-child detection. When the service
    /// finds a child for the same parent with a matching normalized
    /// name + birth year/month, the first attempt returns 409
    /// `duplicate_child` so the parent can choose to open the existing
    /// child or confirm "this is a different child (twins)". The
    /// retry from the dialog sets <c>ConfirmDuplicate=true</c> which
    /// bypasses the dedup check and writes a `duplicate_override`
    /// audit row so the override is traceable.
    /// </summary>
    bool ConfirmDuplicate = false);

public sealed record UpdateChildCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    Guid ChildUserId,
    string? FullName,
    string? FullNameEn,
    int? Grade,
    string? Gender,
    DateTime? Birthday,
    string IpAddress,
    string? UserAgent,
    string CorrelationId,
    string? Username = null,
    // Profile-only fields surfaced by the EditChildDrawer (avatar +
    // curriculum + school). Each is optional; null means "leave as-is",
    // empty string clears the value (school name only — emoji and
    // curriculum cannot be cleared because they are seeded at create).
    string? AvatarEmoji = null,
    string? AvatarBgColor = null,
    string? CurriculumType = null,
    string? SchoolName = null);

public sealed record UnlockChildCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    Guid ChildUserId,
    string ParentPassword,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record ChangePinCommand(
    Guid ChildUserId,
    string CurrentPin,
    string NewPin,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record RegenerateChildPasswordCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    Guid ChildUserId,
    string? CustomPassword,
    string PasswordLocale,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record DeleteChildCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    Guid ChildUserId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

// ── Phase 9 Phase 3: parent-driven credential reset / tier-upgrade ──

/// <summary>
/// Parent resets the 4-digit PIN for an 8–12 child. Child must be on
/// the <c>pin</c> tier; goes through re-auth + weak-PIN blocklist.
/// </summary>
public sealed record ResetChildPinCommand(
    Guid ParentUserId,
    Guid ChildUserId,
    string NewPin,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

/// <summary>
/// Parent adds a PIN for a child who turned 8 (transitions from
/// <c>profile_switch_only</c> to <c>pin</c>). Goes through re-auth +
/// weak-PIN blocklist.
/// </summary>
public sealed record AddChildPinCommand(
    Guid ParentUserId,
    Guid ChildUserId,
    string NewPin,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

/// <summary>
/// Parent upgrades an 8–12 PIN child to the 13+ password tier on
/// their 13th birthday. Clears PinHash, sets the password hash, and
/// flips <c>LoginMethod</c> to <c>username_password</c> in one
/// concurrency-safe mutation.
/// </summary>
public sealed record UpgradeChildToPasswordCommand(
    Guid ParentUserId,
    Guid ChildUserId,
    string NewPassword,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

/// <summary>
/// Parent step-up re-auth — issues a 5-minute freshness receipt so
/// destructive credential actions don't prompt for password+TOTP on
/// every click.
/// </summary>
public sealed record ParentReAuthCommand(
    Guid ParentUserId,
    string Password,
    string? TotpCode,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed class CreateChildCommandValidator : ICommandValidator<CreateChildCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(CreateChildCommand c)
    {
        var errors = new List<ApiResponseError>();

        if (string.IsNullOrWhiteSpace(c.FullName))
            errors.Add(new ApiResponseError { Code = "full_name_required", Field = "fullName", Message = "اسم الطفل مطلوب." });
        else if (c.FullName.Length > ValidationRules.MaxFullNameLength)
            errors.Add(new ApiResponseError { Code = "full_name_length", Field = "fullName", Message = "الاسم طويل جدًا." });

        // KG1 (-1), KG2 (0), Grades 1..12 inclusive.
        if (c.Grade is < -1 or > 12)
            errors.Add(new ApiResponseError { Code = "grade_invalid", Field = "grade", Message = "الصف الدراسي غير صالح." });

        if (c.Gender is not null && c.Gender is not ("male" or "female"))
            errors.Add(new ApiResponseError { Code = "gender_invalid", Field = "gender", Message = "الجنس غير صالح." });

        var today = DateTime.UtcNow;
        if (c.BirthYear < today.Year - 25 || c.BirthYear > today.Year)
            errors.Add(new ApiResponseError { Code = "birth_year_invalid", Field = "birthYear", Message = "سنة الميلاد غير صالحة." });
        if (c.BirthMonth is < 1 or > 12)
            errors.Add(new ApiResponseError { Code = "birth_month_invalid", Field = "birthMonth", Message = "شهر الميلاد غير صالح." });

        if (c.CurriculumType is not ("Moe" or "LanguageSchool" or "International"))
            errors.Add(new ApiResponseError { Code = "curriculum_type_invalid", Field = "curriculumType", Message = "النظام التعليمي غير صالح." });

        if (string.IsNullOrWhiteSpace(c.AvatarEmoji))
            errors.Add(new ApiResponseError { Code = "avatar_required", Field = "avatarEmoji", Message = "الصورة الرمزية مطلوبة." });
        if (string.IsNullOrWhiteSpace(c.AvatarBgColor) || !System.Text.RegularExpressions.Regex.IsMatch(c.AvatarBgColor, "^#[0-9a-fA-F]{6}$"))
            errors.Add(new ApiResponseError { Code = "avatar_bg_color_invalid", Field = "avatarBgColor", Message = "لون الخلفية غير صالح." });

        if (c.PrefLevel is not null && c.PrefLevel is not ("beginner" or "intermediate" or "advanced"))
            errors.Add(new ApiResponseError { Code = "pref_level_invalid", Field = "prefLevel", Message = "المستوى غير صالح." });
        if (c.PrefGoal is not null && c.PrefGoal is not ("improve_level" or "excel" or "review_support"))
            errors.Add(new ApiResponseError { Code = "pref_goal_invalid", Field = "prefGoal", Message = "الهدف غير صالح." });

        if (!c.ParentalConsentAcknowledged)
            errors.Add(new ApiResponseError { Code = "parental_consent_required", Field = "parentalConsentAcknowledged", Message = "يجب الموافقة على شروط الخصوصية لحساب الطفل." });

        if (c.LoginMethod is not ("profile_switch_only" or "pin" or "username_password"))
        {
            errors.Add(new ApiResponseError { Code = "login_method_invalid", Field = "loginMethod", Message = "طريقة تسجيل الدخول غير صالحة." });
            return errors;
        }

        if (c.LoginMethod == "pin")
        {
            if (string.IsNullOrWhiteSpace(c.Pin) || !System.Text.RegularExpressions.Regex.IsMatch(c.Pin, "^[0-9]{4}$"))
                errors.Add(new ApiResponseError { Code = "pin_invalid", Field = "pin", Message = "رمز PIN يجب أن يكون 4 أرقام." });
        }

        if (c.LoginMethod == "username_password")
        {
            if (!string.IsNullOrWhiteSpace(c.PreferredUsername))
            {
                var u = c.PreferredUsername.Trim();
                if (u.Length < 4 || u.Length > 20)
                    errors.Add(new ApiResponseError { Code = "username_length", Field = "preferredUsername", Message = "اسم المستخدم يجب أن يتراوح بين 4 و20 حرفًا." });
                else if (!System.Text.RegularExpressions.Regex.IsMatch(u, "^[a-zA-Z0-9_]+$"))
                    errors.Add(new ApiResponseError { Code = "username_format", Field = "preferredUsername", Message = "اسم المستخدم يجب أن يحتوي على أحرف إنجليزية وأرقام وشرطة سفلية فقط." });
            }
            if (!string.IsNullOrEmpty(c.CustomPassword))
            {
                if (c.CustomPassword.Length < ValidationRules.MinPasswordLength || c.CustomPassword.Length > ValidationRules.MaxPasswordLength)
                    errors.Add(new ApiResponseError { Code = "password_length", Field = "customPassword", Message = $"كلمة المرور يجب أن تتراوح بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
            }
        }

        return errors;
    }
}

public sealed class UpdateChildCommandValidator : ICommandValidator<UpdateChildCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(UpdateChildCommand c)
    {
        var errors = new List<ApiResponseError>();

        if (c.ChildUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." });

        if (c.FullName is not null && string.IsNullOrWhiteSpace(c.FullName))
            errors.Add(new ApiResponseError { Code = "full_name_required", Field = "fullName", Message = "اسم الطفل مطلوب." });
        if (c.FullName is { } fn && fn.Length > ValidationRules.MaxFullNameLength)
            errors.Add(new ApiResponseError { Code = "full_name_length", Field = "fullName", Message = "الاسم طويل جدًا." });

        // Accept KG1 (-1), KG2 (0), and Grades 1..12 inclusive.
        if (c.Grade is { } g && (g < -1 || g > 12))
            errors.Add(new ApiResponseError { Code = "grade_invalid", Field = "grade", Message = "الصف الدراسي غير صالح." });

        if (c.Gender is not null && c.Gender is not ("male" or "female"))
            errors.Add(new ApiResponseError { Code = "gender_invalid", Field = "gender", Message = "الجنس غير صالح." });

        // A null birthday, or the sentinel DateTime.MinValue, means "don't
        // change" — skip the range check. Only a genuine value is scrutinised.
        var today = DateTime.UtcNow.Date;
        if (c.Birthday is { } bd && bd != default
            && (bd.Date >= today || bd.Year < today.Year - 25))
            errors.Add(new ApiResponseError { Code = "birthday_invalid", Field = "birthday", Message = "تاريخ الميلاد غير صالح." });

        // Add-child redesign decision #5: parent can change child's username
        // post-creation. Same regex as create-time (4–20 chars, [a-zA-Z0-9_]).
        if (!string.IsNullOrWhiteSpace(c.Username))
        {
            var u = c.Username.Trim();
            if (u.Length < 4 || u.Length > 20)
                errors.Add(new ApiResponseError { Code = "username_length", Field = "username", Message = "اسم المستخدم يجب أن يتراوح بين 4 و20 حرفًا." });
            else if (!System.Text.RegularExpressions.Regex.IsMatch(u, "^[a-zA-Z0-9_]+$"))
                errors.Add(new ApiResponseError { Code = "username_format", Field = "username", Message = "اسم المستخدم يجب أن يحتوي على أحرف إنجليزية وأرقام وشرطة سفلية فقط." });
        }

        return errors;
    }
}

public sealed class UnlockChildCommandValidator : ICommandValidator<UnlockChildCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(UnlockChildCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.ChildUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." });
        if (string.IsNullOrEmpty(c.ParentPassword))
            errors.Add(new ApiResponseError { Code = "parent_password_required", Field = "parentPassword", Message = "كلمة مرور ولي الأمر مطلوبة." });
        return errors;
    }
}

public sealed class ChangePinCommandValidator : ICommandValidator<ChangePinCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(ChangePinCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrEmpty(c.CurrentPin) || !System.Text.RegularExpressions.Regex.IsMatch(c.CurrentPin, "^[0-9]{4}$"))
            errors.Add(new ApiResponseError { Code = "current_pin_invalid", Field = "currentPin", Message = "رمز PIN الحالي يجب أن يكون 4 أرقام." });
        if (string.IsNullOrEmpty(c.NewPin) || !System.Text.RegularExpressions.Regex.IsMatch(c.NewPin, "^[0-9]{4}$"))
            errors.Add(new ApiResponseError { Code = "new_pin_invalid", Field = "newPin", Message = "رمز PIN الجديد يجب أن يكون 4 أرقام." });
        return errors;
    }
}

public sealed class RegenerateChildPasswordCommandValidator : ICommandValidator<RegenerateChildPasswordCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(RegenerateChildPasswordCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.ChildUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." });
        if (c.PasswordLocale is not ("ar" or "en"))
            errors.Add(new ApiResponseError { Code = "password_locale_invalid", Field = "passwordLocale", Message = "لغة كلمة المرور غير مدعومة." });
        if (!string.IsNullOrEmpty(c.CustomPassword))
        {
            if (c.CustomPassword.Length < ValidationRules.MinPasswordLength || c.CustomPassword.Length > ValidationRules.MaxPasswordLength)
                errors.Add(new ApiResponseError { Code = "password_length", Field = "customPassword", Message = $"كلمة المرور يجب أن تتراوح بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
        }
        return errors;
    }
}

public sealed class DeleteChildCommandValidator : ICommandValidator<DeleteChildCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(DeleteChildCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.ChildUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." });
        return errors;
    }
}

// ── Phase 9 Phase 3 validators ─────────────────────────────────────

/// <summary>Shared shape check for any 4-digit PIN. Strength check (blocklist) lives in the service.</summary>
internal static class PinFormat
{
    public static readonly System.Text.RegularExpressions.Regex FourDigit = new("^[0-9]{4}$");
}

public sealed class ResetChildPinCommandValidator : ICommandValidator<ResetChildPinCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(ResetChildPinCommand c)
        => ValidatePin(c.ChildUserId, c.NewPin);

    internal static IReadOnlyList<ApiResponseError> ValidatePin(Guid childId, string newPin)
    {
        var errors = new List<ApiResponseError>();
        if (childId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." });
        if (string.IsNullOrEmpty(newPin) || !PinFormat.FourDigit.IsMatch(newPin))
            errors.Add(new ApiResponseError { Code = "new_pin_invalid", Field = "newPin", Message = "رمز PIN الجديد يجب أن يكون 4 أرقام." });
        return errors;
    }
}

public sealed class AddChildPinCommandValidator : ICommandValidator<AddChildPinCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(AddChildPinCommand c)
        => ResetChildPinCommandValidator.ValidatePin(c.ChildUserId, c.NewPin);
}

public sealed class UpgradeChildToPasswordCommandValidator : ICommandValidator<UpgradeChildToPasswordCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(UpgradeChildToPasswordCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.ChildUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." });
        // Strength check (zxcvbn) lives in the service against the child's own user-inputs.
        if (string.IsNullOrEmpty(c.NewPassword) || c.NewPassword.Length < ValidationRules.MinPasswordLength || c.NewPassword.Length > ValidationRules.MaxPasswordLength)
            errors.Add(new ApiResponseError { Code = "password_length", Field = "newPassword", Message = $"كلمة المرور يجب أن تتراوح بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
        return errors;
    }
}

public sealed class ParentReAuthCommandValidator : ICommandValidator<ParentReAuthCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(ParentReAuthCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrEmpty(c.Password))
            errors.Add(new ApiResponseError { Code = "password_required", Field = "password", Message = "كلمة المرور مطلوبة." });
        return errors;
    }
}

// ── US5: Parent oversight commands ────────────────────────────────────────

public sealed record SuspendChildCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    Guid ChildUserId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record UnsuspendChildCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    Guid ChildUserId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record RevokeChildSessionCommand(
    Guid ParentUserId,
    Guid ParentTenantId,
    Guid ChildUserId,
    Guid SessionId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed class SuspendChildCommandValidator : ICommandValidator<SuspendChildCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(SuspendChildCommand c)
    {
        if (c.ChildUserId == Guid.Empty)
            return new[] { new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." } };
        return Array.Empty<ApiResponseError>();
    }
}

public sealed class UnsuspendChildCommandValidator : ICommandValidator<UnsuspendChildCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(UnsuspendChildCommand c)
    {
        if (c.ChildUserId == Guid.Empty)
            return new[] { new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." } };
        return Array.Empty<ApiResponseError>();
    }
}

public sealed class RevokeChildSessionCommandValidator : ICommandValidator<RevokeChildSessionCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(RevokeChildSessionCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.ChildUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "child_id_required", Field = "id", Message = "معرّف الطفل مطلوب." });
        if (c.SessionId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "session_id_required", Field = "sessionId", Message = "معرّف الجلسة مطلوب." });
        return errors;
    }
}
