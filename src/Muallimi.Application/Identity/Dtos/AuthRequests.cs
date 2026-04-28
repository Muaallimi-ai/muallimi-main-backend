using System.Text.Json.Serialization;

namespace Muallimi.Application.Identity.Dtos;

/// <summary>
/// T074 — Request bodies for public auth endpoints. Every field name is
/// pinned by <c>identity-http-contract.md</c> and MUST stay camelCase.
/// These are wire DTOs only; the service layer consumes the richer
/// <see cref="Muallimi.Application.Identity.Commands"/> types.
/// </summary>
public sealed class RegisterRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("fullNameEn")]
    public string? FullNameEn { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("acceptedTerms")]
    public bool AcceptedTerms { get; set; }
}

public sealed class LoginRequest
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("rememberMe")]
    public bool RememberMe { get; set; }

    [JsonPropertyName("twoFactorCode")]
    public string? TwoFactorCode { get; set; }

    [JsonPropertyName("tempToken")]
    public string? TempToken { get; set; }
}

/// <summary>Wire body for POST /api/auth/login/pin (8–12 age tier).</summary>
public sealed class PinLoginRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;
}

/// <summary>
/// Wire body for POST /api/auth/lookup-method — auto-switching login.
/// The frontend sends the typed identifier (username or email) and gets
/// back which credential field to render. The endpoint is hardened
/// against username enumeration: the response is deterministic but
/// realistic for unknown identifiers (see PublicAuthEndpoints).
/// </summary>
public sealed class LookupMethodRequest
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = string.Empty;
}

public sealed class RefreshRequest
{
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

public sealed class VerifyEmailRequest
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public sealed class ResendVerificationRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;
}

public sealed class LogoutRequest
{
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }
}
