using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Muallimi.Application.Identity.Dtos;

/// <summary>
/// T074 — Login / refresh success payload. Field names and casing are
/// pinned by <c>specs/009-identity-auth/contracts/identity-http-contract.md</c>
/// §1; the legacy frontend cuts over by flipping a single env var, so
/// any drift breaks the cutover.
/// </summary>
public sealed class AuthResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; init; }

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

    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; init; }

    [JsonPropertyName("twoFactorEnabled")]
    public bool TwoFactorEnabled { get; init; }

    [JsonPropertyName("requiresPasswordReset")]
    public bool RequiresPasswordReset { get; init; }
}

/// <summary>
/// 2FA-required branch of a login response. The caller retries
/// <c>/api/auth/login</c> with <c>tempToken</c> + <c>twoFactorCode</c>.
/// </summary>
public sealed class TwoFactorChallengeResponse
{
    [JsonPropertyName("twoFactorRequired")]
    public bool TwoFactorRequired { get; init; } = true;

    [JsonPropertyName("tempToken")]
    public string TempToken { get; init; } = string.Empty;
}
