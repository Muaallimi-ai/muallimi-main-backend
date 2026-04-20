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
    string? FullNameEn,
    int Grade,
    string Gender,
    DateTime Birthday,
    string? PreferredUsername,
    string? CustomPassword,
    string PasswordLocale,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

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

public sealed class CreateChildCommandValidator : ICommandValidator<CreateChildCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(CreateChildCommand c)
    {
        var errors = new List<ApiResponseError>();

        if (string.IsNullOrWhiteSpace(c.FullName))
            errors.Add(new ApiResponseError { Code = "full_name_required", Field = "fullName", Message = "اسم الطفل مطلوب." });
        else if (c.FullName.Length > ValidationRules.MaxFullNameLength)
            errors.Add(new ApiResponseError { Code = "full_name_length", Field = "fullName", Message = "الاسم طويل جدًا." });

        if (c.Grade is < 1 or > 12)
            errors.Add(new ApiResponseError { Code = "grade_invalid", Field = "grade", Message = "الصف الدراسي غير صالح." });

        if (c.Gender is not ("male" or "female"))
            errors.Add(new ApiResponseError { Code = "gender_invalid", Field = "gender", Message = "الجنس غير صالح." });

        var today = DateTime.UtcNow.Date;
        if (c.Birthday == default)
            errors.Add(new ApiResponseError { Code = "birthday_required", Field = "birthday", Message = "تاريخ الميلاد مطلوب." });
        else if (c.Birthday.Date >= today)
            errors.Add(new ApiResponseError { Code = "birthday_invalid", Field = "birthday", Message = "تاريخ الميلاد غير صالح." });
        else if (c.Birthday.Year < today.Year - 25)
            errors.Add(new ApiResponseError { Code = "birthday_invalid", Field = "birthday", Message = "تاريخ الميلاد غير صالح." });

        if (c.PasswordLocale is not ("ar" or "en"))
            errors.Add(new ApiResponseError { Code = "password_locale_invalid", Field = "passwordLocale", Message = "لغة كلمة المرور غير مدعومة." });

        if (!string.IsNullOrWhiteSpace(c.PreferredUsername))
        {
            var u = c.PreferredUsername.Trim();
            if (u.Length < 3 || u.Length > 50)
                errors.Add(new ApiResponseError { Code = "username_length", Field = "preferredUsername", Message = "اسم المستخدم يجب أن يتراوح بين 3 و50 حرفًا." });
            else if (!System.Text.RegularExpressions.Regex.IsMatch(u, "^[a-z0-9._-]+$"))
                errors.Add(new ApiResponseError { Code = "username_format", Field = "preferredUsername", Message = "اسم المستخدم يجب أن يحتوي على أحرف إنجليزية صغيرة وأرقام فقط." });
        }

        if (!string.IsNullOrEmpty(c.CustomPassword))
        {
            if (c.CustomPassword.Length < ValidationRules.MinPasswordLength || c.CustomPassword.Length > ValidationRules.MaxPasswordLength)
                errors.Add(new ApiResponseError { Code = "password_length", Field = "customPassword", Message = $"كلمة المرور يجب أن تتراوح بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
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

        if (c.Grade is { } g && (g < 1 || g > 12))
            errors.Add(new ApiResponseError { Code = "grade_invalid", Field = "grade", Message = "الصف الدراسي غير صالح." });

        if (c.Gender is not null && c.Gender is not ("male" or "female"))
            errors.Add(new ApiResponseError { Code = "gender_invalid", Field = "gender", Message = "الجنس غير صالح." });

        var today = DateTime.UtcNow.Date;
        if (c.Birthday is { } bd && (bd.Date >= today || bd.Year < today.Year - 25))
            errors.Add(new ApiResponseError { Code = "birthday_invalid", Field = "birthday", Message = "تاريخ الميلاد غير صالح." });

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
