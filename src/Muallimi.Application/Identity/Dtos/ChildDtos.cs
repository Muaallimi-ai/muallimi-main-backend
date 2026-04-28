using System;
using System.Text.Json.Serialization;

namespace Muallimi.Application.Identity.Dtos;

/// <summary>
/// T091 — DTOs for the parent-children surface (US2).
///
/// <see cref="ChildCredentialsOnce"/> is the only shape that carries a
/// plaintext <c>generatedPassword</c> and is returned exclusively by the
/// create + regenerate-password endpoints. Every other read path returns
/// <see cref="ChildSummary"/> or <see cref="ChildDetail"/>, neither of
/// which carry the password — satisfying the "shown exactly once"
/// invariant pinned by the US2 contract test.
/// </summary>
public sealed class ChildSummary
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("avatarEmoji")]
    public string? AvatarEmoji { get; set; }

    [JsonPropertyName("avatarBgColor")]
    public string? AvatarBgColor { get; set; }

    [JsonPropertyName("loginMethod")]
    public string? LoginMethod { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public sealed class ChildDetail
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("birthYear")]
    public int? BirthYear { get; set; }

    [JsonPropertyName("birthMonth")]
    public int? BirthMonth { get; set; }

    [JsonPropertyName("curriculumType")]
    public string? CurriculumType { get; set; }

    [JsonPropertyName("schoolName")]
    public string? SchoolName { get; set; }

    [JsonPropertyName("avatarEmoji")]
    public string? AvatarEmoji { get; set; }

    [JsonPropertyName("avatarBgColor")]
    public string? AvatarBgColor { get; set; }

    [JsonPropertyName("prefLevel")]
    public string? PrefLevel { get; set; }

    [JsonPropertyName("prefStyles")]
    public string? PrefStyles { get; set; }

    [JsonPropertyName("prefGoal")]
    public string? PrefGoal { get; set; }

    [JsonPropertyName("loginMethod")]
    public string? LoginMethod { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("locale")]
    public string Locale { get; set; } = "ar";

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("managedByUserId")]
    public string ManagedByUserId { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Returned exclusively by POST /api/auth/parent/children and
/// POST /api/auth/parent/children/{id}/regenerate-password.
///
/// In the redesigned add-child flow the parent SETS the credential
/// in step 5 of the wizard (PIN or username+password), so
/// <see cref="GeneratedPassword"/> is null when the parent supplied
/// their own. It is only populated as a fallback for legacy callers
/// that do not specify a custom password under "username_password".
/// </summary>
public sealed class ChildCredentialsOnce
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>"avatar_only" | "pin" | "username_password".</summary>
    [JsonPropertyName("loginMethod")]
    public string LoginMethod { get; set; } = "username_password";

    /// <summary>
    /// Plaintext password — populated only for username_password method
    /// when the parent did NOT supply a custom password (server generated).
    /// </summary>
    [JsonPropertyName("generatedPassword")]
    public string? GeneratedPassword { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>Wire body for POST /api/auth/parent/children — six-step add-child flow.</summary>
public sealed class CreateChildRequest
{
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("birthYear")]
    public int BirthYear { get; set; }

    [JsonPropertyName("birthMonth")]
    public int BirthMonth { get; set; }

    [JsonPropertyName("curriculumType")]
    public string CurriculumType { get; set; } = "Moe";

    [JsonPropertyName("schoolName")]
    public string? SchoolName { get; set; }

    [JsonPropertyName("avatarEmoji")]
    public string AvatarEmoji { get; set; } = string.Empty;

    [JsonPropertyName("avatarBgColor")]
    public string AvatarBgColor { get; set; } = string.Empty;

    [JsonPropertyName("prefLevel")]
    public string? PrefLevel { get; set; }

    /// <summary>JSON-stringified array, e.g. <c>["videos","exercises"]</c>.</summary>
    [JsonPropertyName("prefStyles")]
    public string? PrefStyles { get; set; }

    [JsonPropertyName("prefGoal")]
    public string? PrefGoal { get; set; }

    [JsonPropertyName("loginMethod")]
    public string LoginMethod { get; set; } = "username_password";

    [JsonPropertyName("pin")]
    public string? Pin { get; set; }

    [JsonPropertyName("preferredUsername")]
    public string? PreferredUsername { get; set; }

    [JsonPropertyName("customPassword")]
    public string? CustomPassword { get; set; }

    /// <summary>
    /// Step 6 explicit consent checkbox. Must be <c>true</c> for the
    /// command to validate (see <c>CreateChildCommandValidator</c>).
    /// Persisted as a <c>ParentalConsent</c> row keyed by parent+child.
    /// </summary>
    [JsonPropertyName("parentalConsentAcknowledged")]
    public bool ParentalConsentAcknowledged { get; set; }
}

/// <summary>Wire body for PATCH /api/auth/parent/children/{id}.</summary>
public sealed class UpdateChildRequest
{
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("fullNameEn")]
    public string? FullNameEn { get; set; }

    [JsonPropertyName("grade")]
    public int? Grade { get; set; }

    [JsonPropertyName("gender")]
    public string? Gender { get; set; }

    [JsonPropertyName("birthday")]
    public DateTime? Birthday { get; set; }

    /// <summary>
    /// Optional username change. Re-checked for global uniqueness; if
    /// accepted, ALL child sessions are revoked (access + refresh).
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

/// <summary>Wire body for POST /api/auth/parent/children/{id}/unlock.</summary>
public sealed class UnlockChildRequest
{
    [JsonPropertyName("parentPassword")]
    public string ParentPassword { get; set; } = string.Empty;
}

/// <summary>Wire body for POST /api/auth/change-pin (8-12 child tier).</summary>
public sealed class ChangePinRequest
{
    [JsonPropertyName("currentPin")]
    public string CurrentPin { get; set; } = string.Empty;

    [JsonPropertyName("newPin")]
    public string NewPin { get; set; } = string.Empty;
}

/// <summary>
/// Wire body for POST /api/auth/parent/children/{id}/regenerate-password.
/// </summary>
public sealed class RegenerateChildPasswordRequest
{
    [JsonPropertyName("customPassword")]
    public string? CustomPassword { get; set; }

    [JsonPropertyName("passwordLocale")]
    public string? PasswordLocale { get; set; }
}

// ── US5: Parent oversight DTOs ────────────────────────────────────────────

/// <summary>Active session summary for a child (US5 T144).</summary>
public sealed class ChildSessionSummary
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("deviceType")]
    public string DeviceType { get; set; } = string.Empty;

    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = string.Empty;

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("lastSeenAt")]
    public DateTime LastSeenAt { get; set; }
}

/// <summary>Single row in a child's login history (US5 T144).</summary>
public sealed class ChildLoginHistoryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ipAddress")]
    public string IpAddress { get; set; } = string.Empty;

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; set; }

    [JsonPropertyName("attemptedAt")]
    public DateTime AttemptedAt { get; set; }
}
