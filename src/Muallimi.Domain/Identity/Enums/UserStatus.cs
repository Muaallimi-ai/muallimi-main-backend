namespace Muallimi.Domain.Identity.Enums;

/// <summary>
/// User lifecycle states. Transitions are enforced on <see cref="Entities.User"/>.
/// <para>
/// <c>PendingEmailVerification</c> — freshly registered Personal account;
/// cannot log in until the verification token is consumed (FR-006).
/// </para>
/// <para>
/// <c>Active</c> — ordinary logged-in state.
/// </para>
/// <para>
/// <c>Suspended</c> — admin action; cannot log in until unsuspended.
/// </para>
/// <para>
/// <c>PasswordResetRequired</c> — forced password change on next login
/// (e.g., super-admin 90-day rotation, or admin-initiated reset).
/// </para>
/// <para>
/// <c>Locked</c> — temporary lockout after 5 failed logins; cleared when
/// <c>LockoutEnd</c> elapses or a successful password reset occurs.
/// </para>
/// <para>
/// <c>Archived</c> — soft delete terminal state; no login, no impersonation.
/// </para>
/// </summary>
public enum UserStatus
{
    PendingEmailVerification = 1,
    Active = 2,
    Suspended = 3,
    PasswordResetRequired = 4,
    Locked = 5,
    Archived = 6,
}
