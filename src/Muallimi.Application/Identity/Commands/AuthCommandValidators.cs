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

public static class ValidationRules
{
    // Keep permissive but sane — matches zxcvbn's own hint bar.
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxFullNameLength = 100;
    public const int MaxEmailLength = 255;

    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Egyptian mobile: optional +20 or leading 0, then 1 + [0|1|2|5] +
    /// 8 digits. Mirrors the frontend <c>phone</c> validator in
    /// <c>src/lib/auth/validators.ts</c>.
    /// </summary>
    public static readonly Regex EgyptianPhoneRegex = new(
        @"^(?:\+20|0)?1[0-25][0-9]{8}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValidEmail(string? email)
        => !string.IsNullOrWhiteSpace(email)
           && email.Length <= MaxEmailLength
           && EmailRegex.IsMatch(email);

    public static bool IsValidLocale(string? locale)
        => locale is "ar" or "en";

    /// <summary>
    /// Returns <c>true</c> if <paramref name="phone"/> (after stripping
    /// spaces) matches <see cref="EgyptianPhoneRegex"/>.
    /// </summary>
    public static bool IsValidPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;
        var stripped = StripWhitespace(phone);
        return EgyptianPhoneRegex.IsMatch(stripped);
    }

    /// <summary>
    /// Normalizes an Egyptian mobile number to its 10-digit core
    /// beginning with <c>1</c>. Strips any leading <c>+20</c>, leading
    /// <c>0</c>, and whitespace. Returns <c>null</c> if the phone is
    /// null/empty or does not match the Egyptian mobile regex.
    /// </summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var stripped = StripWhitespace(phone);
        if (!EgyptianPhoneRegex.IsMatch(stripped)) return null;
        if (stripped.StartsWith("+20", StringComparison.Ordinal))
        {
            stripped = stripped.Substring(3);
        }
        if (stripped.StartsWith("0", StringComparison.Ordinal))
        {
            stripped = stripped.Substring(1);
        }
        return stripped;
    }

    private static string StripWhitespace(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        System.Text.StringBuilder? sb = null;
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c))
            {
                sb ??= new System.Text.StringBuilder(input.Length).Append(input, 0, i);
                continue;
            }
            sb?.Append(c);
        }
        return sb?.ToString() ?? input;
    }
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

        if (string.IsNullOrWhiteSpace(c.PhoneNumber))
            errors.Add(new ApiResponseError { Code = "phone_required", Field = "phoneNumber", Message = "رقم الهاتف مطلوب." });
        else if (!ValidationRules.IsValidPhone(c.PhoneNumber))
            errors.Add(new ApiResponseError { Code = "phone_invalid", Field = "phoneNumber", Message = "رقم الهاتف غير صالح." });

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
        if (string.IsNullOrWhiteSpace(c.PhoneNumber))
            errors.Add(new ApiResponseError { Code = "phone_required", Field = "phoneNumber", Message = "رقم الهاتف مطلوب." });
        else if (!ValidationRules.IsValidPhone(c.PhoneNumber))
            errors.Add(new ApiResponseError { Code = "phone_invalid", Field = "phoneNumber", Message = "رقم الهاتف غير صالح." });
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
