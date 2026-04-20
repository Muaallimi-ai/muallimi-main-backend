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

    [JsonPropertyName("fullNameEn")]
    public string? FullNameEn { get; set; }

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("gender")]
    public string Gender { get; set; } = string.Empty;

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

    [JsonPropertyName("fullNameEn")]
    public string? FullNameEn { get; set; }

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("gender")]
    public string Gender { get; set; } = string.Empty;

    [JsonPropertyName("birthday")]
    public DateTime Birthday { get; set; }

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
/// POST /api/auth/parent/children/{id}/regenerate-password. The
/// plaintext password is not stored on the User entity — only the
/// BCrypt hash is persisted — so this envelope is the single moment
/// the parent ever sees the generated password.
/// </summary>
public sealed class ChildCredentialsOnce
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("generatedPassword")]
    public string GeneratedPassword { get; set; } = string.Empty;

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>Wire body for POST /api/auth/parent/children.</summary>
public sealed class CreateChildRequest
{
    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("fullNameEn")]
    public string? FullNameEn { get; set; }

    [JsonPropertyName("grade")]
    public int Grade { get; set; }

    [JsonPropertyName("gender")]
    public string Gender { get; set; } = string.Empty;

    [JsonPropertyName("birthday")]
    public DateTime Birthday { get; set; }

    [JsonPropertyName("preferredUsername")]
    public string? PreferredUsername { get; set; }

    [JsonPropertyName("customPassword")]
    public string? CustomPassword { get; set; }

    [JsonPropertyName("passwordLocale")]
    public string? PasswordLocale { get; set; }
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
