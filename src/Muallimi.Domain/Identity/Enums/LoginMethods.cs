namespace Muallimi.Domain.Identity.Enums;

/// <summary>
/// Stable wire values for the per-Managed-account credential tier
/// (<see cref="Muallimi.Domain.Identity.Entities.User.LoginMethod"/>).
///
/// Centralised here so the constants are single-sourced — never write
/// the literal string in command validators, JWT claim builders, or
/// frontend-facing DTOs.
///
/// Personal accounts (parents, school admins, teachers, operators)
/// have <c>LoginMethod = null</c>.
/// </summary>
public static class LoginMethods
{
    /// <summary>Under-8 children: no credential, parent picks them from the family screen.</summary>
    public const string ProfileSwitchOnly = "profile_switch_only";

    /// <summary>Ages 8–12: 4-digit PIN, parent-managed.</summary>
    public const string Pin = "pin";

    /// <summary>Ages 13+: username + password, child-managed (with parent reset).</summary>
    public const string UsernamePassword = "username_password";

    /// <summary>True if the value is one of the recognised tier strings.</summary>
    public static bool IsValid(string? value) =>
        value is ProfileSwitchOnly or Pin or UsernamePassword;
}
