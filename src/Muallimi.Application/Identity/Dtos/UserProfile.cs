using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Muallimi.Application.Identity.Dtos;

/// <summary>
/// T074 — Response payload for <c>GET /api/auth/me</c>. Frontend
/// <c>useAuth</c> hydrates this on mount and again after refresh, so
/// every claim shown in the UI (roles, locale, email-verified badge) is
/// sourced from this single DTO.
/// </summary>
public sealed class UserProfile
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("fullName")]
    public string FullName { get; init; } = string.Empty;

    [JsonPropertyName("fullNameEn")]
    public string? FullNameEn { get; init; }

    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("tenantType")]
    public string TenantType { get; init; } = string.Empty;

    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("locale")]
    public string Locale { get; init; } = "ar";

    [JsonPropertyName("accountType")]
    public string AccountType { get; init; } = "personal";

    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; init; }

    [JsonPropertyName("twoFactorEnabled")]
    public bool TwoFactorEnabled { get; init; }

    [JsonPropertyName("requiresPasswordReset")]
    public bool RequiresPasswordReset { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}
