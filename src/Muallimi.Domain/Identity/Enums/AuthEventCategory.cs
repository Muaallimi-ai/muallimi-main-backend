namespace Muallimi.Domain.Identity.Enums;

/// <summary>
/// 15-category audit vocabulary for identity events (contract
/// <c>identity.audit.events</c>). Each value maps to the
/// <c>EventCategory</c> string written on <see cref="Muallimi.Application.Audit.AuditEvent"/>
/// when an Identity action is emitted.
/// </summary>
public enum AuthEventCategory
{
    Register = 1,
    Login = 2,
    Logout = 3,
    PasswordChange = 4,
    PasswordReset = 5,
    EmailVerified = 6,
    TwoFactorEnabled = 7,
    TwoFactorDisabled = 8,
    RoleGranted = 9,
    RoleRevoked = 10,
    AccountSuspended = 11,
    AccountUnsuspended = 12,
    AccountDeleted = 13,
    Impersonation = 14,
    SessionRevoked = 15,
}
