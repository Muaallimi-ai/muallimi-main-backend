namespace Muallimi.Domain.Identity.Enums;

/// <summary>
/// Distinguishes self-managed login accounts (Personal — parent, school-admin,
/// teacher, operators) from accounts created and managed by another user
/// (Managed — students created by their parent or by school roster import).
/// </summary>
public enum AccountType
{
    Personal = 1,
    Managed = 2,
}
