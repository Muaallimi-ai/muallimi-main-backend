using System;
using System.Collections.Generic;
using Muallimi.Application.Identity.Dtos;

namespace Muallimi.Application.Identity.Commands;

/// <summary>
/// T110 — Admin command records for the super-admin / platform-operator
/// surface. Every command carries the acting admin's user + tenant id,
/// roles (so privilege-escalation checks can run in the service), and
/// the standard request context (IP, user agent, correlation id).
/// </summary>
public sealed record InviteUserCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    IReadOnlyList<string> ActorRoles,
    string Email,
    string FullName,
    string? FullNameEn,
    string Locale,
    IReadOnlyList<string> Roles,
    Guid? TargetTenantId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record GrantRoleCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    IReadOnlyList<string> ActorRoles,
    Guid TargetUserId,
    string RoleName,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record RevokeRoleCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    IReadOnlyList<string> ActorRoles,
    Guid TargetUserId,
    string RoleName,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record SuspendUserCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    IReadOnlyList<string> ActorRoles,
    Guid TargetUserId,
    string? Reason,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record UnsuspendUserCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    IReadOnlyList<string> ActorRoles,
    Guid TargetUserId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record DeleteUserCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    IReadOnlyList<string> ActorRoles,
    Guid TargetUserId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record AdminResetPasswordCommand(
    Guid ActorUserId,
    Guid ActorTenantId,
    IReadOnlyList<string> ActorRoles,
    Guid TargetUserId,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record ListUsersQuery(
    Guid ActorUserId,
    IReadOnlyList<string> ActorRoles,
    Guid? TenantId,
    string? RoleName,
    string? Status,
    string? Search,
    int Page,
    int PageSize);

public sealed record GetAuditLogQuery(
    Guid ActorUserId,
    IReadOnlyList<string> ActorRoles,
    Guid? TenantId,
    Guid? TargetActorId,
    Guid? TargetUserId,
    string? Category,
    string? Outcome,
    DateTime? From,
    DateTime? To,
    string? Cursor,
    int Limit);

public sealed record ChangePasswordCommand(
    Guid UserId,
    string CurrentPassword,
    string NewPassword,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

public sealed record AcceptInvitationCommand(
    string Token,
    string NewPassword,
    string IpAddress,
    string? UserAgent,
    string CorrelationId);

// ── Validators ────────────────────────────────────────────────────────

public sealed class InviteUserCommandValidator : ICommandValidator<InviteUserCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(InviteUserCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (!ValidationRules.IsValidEmail(c.Email))
            errors.Add(new ApiResponseError { Code = "email_invalid", Field = "email", Message = "البريد الإلكتروني غير صالح." });
        if (string.IsNullOrWhiteSpace(c.FullName))
            errors.Add(new ApiResponseError { Code = "full_name_required", Field = "fullName", Message = "الاسم الكامل مطلوب." });
        else if (c.FullName.Length > ValidationRules.MaxFullNameLength)
            errors.Add(new ApiResponseError { Code = "full_name_length", Field = "fullName", Message = "الاسم طويل جدًا." });
        if (!ValidationRules.IsValidLocale(c.Locale))
            errors.Add(new ApiResponseError { Code = "locale_invalid", Field = "locale", Message = "اللغة غير مدعومة." });
        if (c.Roles is null || c.Roles.Count == 0)
            errors.Add(new ApiResponseError { Code = "role_required", Field = "roles", Message = "يجب تحديد دور واحد على الأقل." });
        return errors;
    }
}

public sealed class GrantRoleCommandValidator : ICommandValidator<GrantRoleCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(GrantRoleCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.TargetUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "id", Message = "معرّف المستخدم مطلوب." });
        if (string.IsNullOrWhiteSpace(c.RoleName))
            errors.Add(new ApiResponseError { Code = "role_required", Field = "roleName", Message = "اسم الدور مطلوب." });
        return errors;
    }
}

public sealed class RevokeRoleCommandValidator : ICommandValidator<RevokeRoleCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(RevokeRoleCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.TargetUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "id", Message = "معرّف المستخدم مطلوب." });
        if (string.IsNullOrWhiteSpace(c.RoleName))
            errors.Add(new ApiResponseError { Code = "role_required", Field = "roleName", Message = "اسم الدور مطلوب." });
        return errors;
    }
}

public sealed class SuspendUserCommandValidator : ICommandValidator<SuspendUserCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(SuspendUserCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.TargetUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "id", Message = "معرّف المستخدم مطلوب." });
        return errors;
    }
}

public sealed class UnsuspendUserCommandValidator : ICommandValidator<UnsuspendUserCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(UnsuspendUserCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.TargetUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "id", Message = "معرّف المستخدم مطلوب." });
        return errors;
    }
}

public sealed class DeleteUserCommandValidator : ICommandValidator<DeleteUserCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(DeleteUserCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.TargetUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "id", Message = "معرّف المستخدم مطلوب." });
        return errors;
    }
}

public sealed class AdminResetPasswordCommandValidator : ICommandValidator<AdminResetPasswordCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(AdminResetPasswordCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (c.TargetUserId == Guid.Empty)
            errors.Add(new ApiResponseError { Code = "user_required", Field = "id", Message = "معرّف المستخدم مطلوب." });
        return errors;
    }
}

public sealed class ChangePasswordCommandValidator : ICommandValidator<ChangePasswordCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(ChangePasswordCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrWhiteSpace(c.CurrentPassword))
            errors.Add(new ApiResponseError { Code = "current_password_required", Field = "currentPassword", Message = "كلمة المرور الحالية مطلوبة." });
        if (string.IsNullOrWhiteSpace(c.NewPassword))
            errors.Add(new ApiResponseError { Code = "new_password_required", Field = "newPassword", Message = "كلمة المرور الجديدة مطلوبة." });
        else if (c.NewPassword.Length < ValidationRules.MinPasswordLength || c.NewPassword.Length > ValidationRules.MaxPasswordLength)
            errors.Add(new ApiResponseError { Code = "password_length", Field = "newPassword", Message = $"يجب أن تتراوح كلمة المرور بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
        return errors;
    }
}

public sealed class AcceptInvitationCommandValidator : ICommandValidator<AcceptInvitationCommand>
{
    public IReadOnlyList<ApiResponseError> Validate(AcceptInvitationCommand c)
    {
        var errors = new List<ApiResponseError>();
        if (string.IsNullOrWhiteSpace(c.Token))
            errors.Add(new ApiResponseError { Code = "token_required", Field = "token", Message = "رمز الدعوة مطلوب." });
        if (string.IsNullOrWhiteSpace(c.NewPassword))
            errors.Add(new ApiResponseError { Code = "new_password_required", Field = "newPassword", Message = "كلمة المرور الجديدة مطلوبة." });
        else if (c.NewPassword.Length < ValidationRules.MinPasswordLength || c.NewPassword.Length > ValidationRules.MaxPasswordLength)
            errors.Add(new ApiResponseError { Code = "password_length", Field = "newPassword", Message = $"يجب أن تتراوح كلمة المرور بين {ValidationRules.MinPasswordLength} و{ValidationRules.MaxPasswordLength} حرفًا." });
        return errors;
    }
}
