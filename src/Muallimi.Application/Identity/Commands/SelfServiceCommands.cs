using System;
using System.Collections.Generic;
using Muallimi.Application.Identity.Dtos;

namespace Muallimi.Application.Identity.Commands;

/// <summary>
/// T133 — US4 self-service command records:
///   password self-service (forgot/reset), sessions management,
///   2FA enrolment (enable/verify/disable).
/// </summary>

public sealed record ForgotPasswordCommand(
    string Email,
    string IpAddress,
    string CorrelationId);

public sealed record ResetPasswordCommand(
    string Token,
    string NewPassword,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record EnableTwoFactorCommand(
    Guid UserId,
    string CorrelationId);

public sealed record VerifyTwoFactorCommand(
    Guid UserId,
    string Code,
    string CorrelationId);

public sealed record DisableTwoFactorCommand(
    Guid UserId,
    string CurrentPassword,
    string CorrelationId);

public sealed record ListSessionsQuery(
    Guid UserId);

public sealed record RevokeSessionCommand(
    Guid UserId,
    Guid TargetSessionId,
    string CorrelationId);

// ── Validators ─────────────────────────────────────────────────────────

public sealed class ForgotPasswordCommandValidator : ICommandValidator<ForgotPasswordCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(ForgotPasswordCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (!ValidationRules.IsValidEmail(c.Email))
            errors.Add(new ApiResponseError { Code = "email_invalid", Field = "email", Message = "البريد الإلكتروني غير صالح." });
        return errors;
    }
}

public sealed class ResetPasswordCommandValidator : ICommandValidator<ResetPasswordCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(ResetPasswordCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrWhiteSpace(c.Token))
            errors.Add(new ApiResponseError { Code = "token_required", Field = "token", Message = "رمز إعادة التعيين مطلوب." });
        if (string.IsNullOrWhiteSpace(c.NewPassword))
            errors.Add(new ApiResponseError { Code = "new_password_required", Field = "newPassword", Message = "كلمة المرور الجديدة مطلوبة." });
        else if (c.NewPassword.Length < ValidationRules.MinPasswordLength || c.NewPassword.Length > ValidationRules.MaxPasswordLength)
            errors.Add(new ApiResponseError { Code = "password_length", Field = "newPassword",
                Message = $"يجب أن تتراوح كلمة المرور بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
        return errors;
    }
}

public sealed class EnableTwoFactorCommandValidator : ICommandValidator<EnableTwoFactorCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(EnableTwoFactorCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.UserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "userId", Message = "معرّف المستخدم مطلوب." });
        return errors;
    }
}

public sealed class VerifyTwoFactorCommandValidator : ICommandValidator<VerifyTwoFactorCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(VerifyTwoFactorCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.UserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "userId", Message = "معرّف المستخدم مطلوب." });
        if (string.IsNullOrWhiteSpace(c.Code))
            errors.Add(new ApiResponseError { Code = "code_required", Field = "code", Message = "رمز التحقق مطلوب." });
        return errors;
    }
}

public sealed class DisableTwoFactorCommandValidator : ICommandValidator<DisableTwoFactorCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(DisableTwoFactorCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.UserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "userId", Message = "معرّف المستخدم مطلوب." });
        if (string.IsNullOrWhiteSpace(c.CurrentPassword))
            errors.Add(new ApiResponseError { Code = "password_required", Field = "currentPassword", Message = "كلمة المرور الحالية مطلوبة." });
        return errors;
    }
}

public sealed class RevokeSessionCommandValidator : ICommandValidator<RevokeSessionCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(RevokeSessionCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.UserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "userId", Message = "معرّف المستخدم مطلوب." });
        if (c.TargetSessionId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "session_required", Field = "sessionId", Message = "معرّف الجلسة مطلوب." });
        return errors;
    }
}
