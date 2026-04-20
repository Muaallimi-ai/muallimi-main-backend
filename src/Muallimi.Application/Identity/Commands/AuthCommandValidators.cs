using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Validators;

namespace Muallimi.Application.Identity.Commands;

/// <summary>
/// T073 — Lightweight validators for each <c>AuthCommand</c>. Mirror
/// the FluentValidation shape (field-level errors → envelope) without
/// taking a FluentValidation package dependency. The Arabic + English
/// messages are pinned by the frontend <c>src/lib/auth/validators.ts</c>
/// (T051) — mirror those strings here.
/// </summary>
public interface ICommandValidator<in T>
{
    IReadOnlyList<ApiResponseError> Validate(T command);
}

internal static class ValidationRules
{
    // Keep permissive but sane — matches zxcvbn's own hint bar.
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxFullNameLength = 100;
    public const int MaxEmailLength = 255;

    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidEmail(string? email)
        => !string.IsNullOrWhiteSpace(email)
           && email.Length <= MaxEmailLength
           && EmailRegex.IsMatch(email);

    public static bool IsValidLocale(string? locale)
        => locale is "ar" or "en";
}

public sealed class RegisterParentCommandValidator : ICommandValidator<RegisterParentCommand>
{
    private readonly IPasswordStrengthValidator _strength;

    public RegisterParentCommandValidator(IPasswordStrengthValidator strength)
    {
        _strength = strength;
    }

    public IReadOnlyList<ApiResponseError> Validate(RegisterParentCommand c)
    {
        var errors = new List<ApiResponseError>();

        if (!ValidationRules.IsValidEmail(c.Email))
            errors.Add(new ApiResponseError { Code = "email_invalid", Field = "email", Message = "البريد الإلكتروني غير صالح." });

        if (string.IsNullOrWhiteSpace(c.Password))
            errors.Add(new ApiResponseError { Code = "password_required", Field = "password", Message = "كلمة المرور مطلوبة." });
        else if (c.Password.Length < ValidationRules.MinPasswordLength || c.Password.Length > ValidationRules.MaxPasswordLength)
            errors.Add(new ApiResponseError { Code = "password_length", Field = "password", Message = $"يجب أن تتراوح كلمة المرور بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
        else
        {
            var strength = _strength.Evaluate(c.Password, c.Email ?? string.Empty, c.FullName ?? string.Empty);
            if (!strength.IsAcceptable)
            {
                errors.Add(new ApiResponseError { Code = "password_weak", Field = "password", Message = strength.FeedbackAr });
            }
        }

        if (string.IsNullOrWhiteSpace(c.FullName))
            errors.Add(new ApiResponseError { Code = "full_name_required", Field = "fullName", Message = "الاسم الكامل مطلوب." });
        else if (c.FullName.Length > ValidationRules.MaxFullNameLength)
            errors.Add(new ApiResponseError { Code = "full_name_length", Field = "fullName", Message = "الاسم طويل جدًا." });

        if (!ValidationRules.IsValidLocale(c.Locale))
            errors.Add(new ApiResponseError { Code = "locale_invalid", Field = "locale", Message = "اللغة غير مدعومة." });

        if (!c.AcceptedTerms)
            errors.Add(new ApiResponseError { Code = "terms_not_accepted", Field = "acceptedTerms", Message = "يجب الموافقة على الشروط." });

        return errors;
    }
}

public sealed class RegisterSchoolAdminCommandValidator : ICommandValidator<RegisterSchoolAdminCommand>
{
    private readonly IPasswordStrengthValidator _strength;

    public RegisterSchoolAdminCommandValidator(IPasswordStrengthValidator strength)
    {
        _strength = strength;
    }

    public IReadOnlyList<ApiResponseError> Validate(RegisterSchoolAdminCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (!ValidationRules.IsValidEmail(c.Email))
            errors.Add(new ApiResponseError { Code = "email_invalid", Field = "email", Message = "البريد الإلكتروني غير صالح." });
        if (string.IsNullOrWhiteSpace(c.Password))
            errors.Add(new ApiResponseError { Code = "password_required", Field = "password", Message = "كلمة المرور مطلوبة." });
        else
        {
            var strength = _strength.Evaluate(c.Password, c.Email ?? string.Empty);
            if (!strength.IsAcceptable)
                errors.Add(new ApiResponseError { Code = "password_weak", Field = "password", Message = strength.FeedbackAr });
        }
        if (string.IsNullOrWhiteSpace(c.FullName))
            errors.Add(new ApiResponseError { Code = "full_name_required", Field = "fullName", Message = "الاسم الكامل مطلوب." });
        if (string.IsNullOrWhiteSpace(c.SchoolDisplayName))
            errors.Add(new ApiResponseError { Code = "school_name_required", Field = "schoolDisplayName", Message = "اسم المدرسة مطلوب." });
        if (!ValidationRules.IsValidLocale(c.Locale))
            errors.Add(new ApiResponseError { Code = "locale_invalid", Field = "locale", Message = "اللغة غير مدعومة." });
        if (!c.AcceptedTerms)
            errors.Add(new ApiResponseError { Code = "terms_not_accepted", Field = "acceptedTerms", Message = "يجب الموافقة على الشروط." });
        return errors;
    }
}

public sealed class LoginCommandValidator : ICommandValidator<LoginCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(LoginCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrWhiteSpace(c.Identifier))
            errors.Add(new ApiResponseError { Code = "identifier_required", Field = "identifier", Message = "البريد الإلكتروني أو اسم المستخدم مطلوب." });
        if (string.IsNullOrWhiteSpace(c.Password))
            errors.Add(new ApiResponseError { Code = "password_required", Field = "password", Message = "كلمة المرور مطلوبة." });
        return errors;
    }
}

public sealed class RefreshTokenCommandValidator : ICommandValidator<RefreshTokenCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(RefreshTokenCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrWhiteSpace(c.RefreshToken))
            errors.Add(new ApiResponseError { Code = "refresh_token_required", Field = "refreshToken", Message = "رمز التحديث مطلوب." });
        return errors;
    }
}

public sealed class LogoutCommandValidator : ICommandValidator<LogoutCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(LogoutCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.SessionId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "session_required", Field = "session", Message = "الجلسة مطلوبة." });
        if (c.UserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "user", Message = "المستخدم مطلوب." });
        return errors;
    }
}

public sealed class VerifyEmailCommandValidator : ICommandValidator<VerifyEmailCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(VerifyEmailCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrWhiteSpace(c.Token))
            errors.Add(new ApiResponseError { Code = "token_required", Field = "token", Message = "رمز التحقق مطلوب." });
        return errors;
    }
}

public sealed class ResendVerificationCommandValidator : ICommandValidator<ResendVerificationCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(ResendVerificationCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (!ValidationRules.IsValidEmail(c.Email))
            errors.Add(new ApiResponseError { Code = "email_invalid", Field = "email", Message = "البريد الإلكتروني غير صالح." });
        return errors;
    }
}
