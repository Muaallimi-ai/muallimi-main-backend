namespace Muallimi.Application.Identity.Credentials;

/// <summary>
/// Enumerates every credential-management event the system audits.
/// String values are stable (used as the <c>action_type</c> column in
/// the Phase 6 audit trail) — never rename without a data migration.
///
/// The list is parent-managed-child centric today. When school-managed
/// students gain reset flows (B2B Phase 2+), add new values here
/// (<c>school_admin_reset_student_password</c>, etc.) and emit them
/// through the same <see cref="ICredentialAuditWriter"/> — no new
/// infrastructure required.
/// </summary>
public enum CredentialAuditEventKind
{
    ChildPasswordChangedSelf,
    ParentResetChildPassword,
    ParentResetChildPin,
    ChildPinLocked,
    ParentUnlockedChildPin,
    ParentAddedChildPin,
    ParentUpgradedChildToPassword,
    ParentReauthFailed,
    ChildPasswordChangeRejected,
}

public static class CredentialAuditEventKindExtensions
{
    /// <summary>
    /// Stable wire string for the audit row. Snake-case to match the
    /// Phase 6 audit convention.
    /// </summary>
    public static string ToActionType(this CredentialAuditEventKind kind) => kind switch
    {
        CredentialAuditEventKind.ChildPasswordChangedSelf => "child_password_changed_self",
        CredentialAuditEventKind.ParentResetChildPassword => "parent_reset_child_password",
        CredentialAuditEventKind.ParentResetChildPin => "parent_reset_child_pin",
        CredentialAuditEventKind.ChildPinLocked => "child_pin_locked",
        CredentialAuditEventKind.ParentUnlockedChildPin => "parent_unlocked_child_pin",
        CredentialAuditEventKind.ParentAddedChildPin => "parent_added_child_pin",
        CredentialAuditEventKind.ParentUpgradedChildToPassword => "parent_upgraded_child_to_password",
        CredentialAuditEventKind.ParentReauthFailed => "parent_reauth_failed",
        CredentialAuditEventKind.ChildPasswordChangeRejected => "child_password_change_rejected",
        _ => throw new System.ArgumentOutOfRangeException(nameof(kind), kind, "Unknown credential audit kind."),
    };
}

/// <summary>
/// Reasons attached to <see cref="CredentialAuditEventKind.ChildPasswordChangeRejected"/>.
/// Stored on the audit row's payload.
/// </summary>
public static class ChildPasswordChangeRejectionReasons
{
    public const string WrongCurrent = "wrong_current";
    public const string Weak = "weak";
    public const string Locked = "locked";
}

/// <summary>
/// Reasons attached to <see cref="CredentialAuditEventKind.ChildPinLocked"/>.
/// </summary>
public static class ChildPinLockedReasons
{
    public const string ThreeFailures = "3_failures";
}
