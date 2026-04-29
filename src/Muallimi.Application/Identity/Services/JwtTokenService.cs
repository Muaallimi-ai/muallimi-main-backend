using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T155 — Full impersonation claim object stored in the JWT.
/// Matches the <c>identity.claims</c> contract §Impersonation variant:
/// <c>{ "by": "...", "session": "...", "expires_at": "..." }</c>.
/// </summary>
public sealed record ImpersonationClaim(
    string By,
    string Session,
    DateTime ExpiresAt);

/// <summary>
/// T030 — JWT issuer for Phase 9. HS256, 15-minute access-token TTL by
/// default; every claim in the <c>identity.claims</c> contract is
/// present. Refresh tokens are opaque random strings (see
/// <see cref="GenerateRefreshToken"/>) — not JWTs — and are hashed
/// before storage.
/// </summary>
public interface ITokenService
{
    AccessTokenDto GenerateAccessToken(
        User user,
        TenantType tenantType,
        IReadOnlyCollection<string> roleNames,
        Guid sessionId,
        ImpersonationClaim? impersonation = null,
        IReadOnlyDictionary<string, Guid>? profileIds = null,
        Guid? derivedFromSessionId = null,
        TimeSpan? overrideLifetime = null,
        string? avatarEmoji = null,
        string? avatarBgColor = null);

    (string token, string hash) GenerateRefreshToken();

    ClaimsPrincipal? ValidateAccessToken(string token);
}

public sealed record AccessTokenDto(string Token, DateTime ExpiresAt);

public sealed class JwtTokenServiceOptions
{
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>
    /// Optional previous signing key, accepted by the validator during a
    /// key-rotation window. Minting always uses <see cref="SecretKey"/>.
    /// Rotation playbook: set the new key as <see cref="SecretKey"/>, move
    /// the old key here, wait <see cref="AccessTokenMinutes"/> minutes for
    /// every outstanding access token to expire, then unset.
    /// </summary>
    public string? PreviousSecretKey { get; init; }

    public string Issuer { get; init; } = "muallimi-main-backend";
    public string Audience { get; init; } = "muallimi-platform";
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 7;

    /// <summary>
    /// Single source of truth for clock-skew tolerance. Both the bearer
    /// middleware and <see cref="JwtTokenService.ValidateAccessToken"/>
    /// read this — they cannot drift.
    /// </summary>
    public static readonly TimeSpan DefaultClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Builds the canonical <see cref="TokenValidationParameters"/> shared
    /// by the ASP.NET JWT-bearer middleware and
    /// <see cref="JwtTokenService.ValidateAccessToken"/>. Accepts the
    /// current <see cref="SecretKey"/> and (when set) the
    /// <see cref="PreviousSecretKey"/> for zero-downtime rotation. The
    /// algorithm is pinned to HS256 as defense-in-depth against alg
    /// confusion.
    /// </summary>
    public TokenValidationParameters CreateValidationParameters()
    {
        if (string.IsNullOrWhiteSpace(SecretKey))
        {
            throw new InvalidOperationException("JwtTokenServiceOptions.SecretKey must be set.");
        }
        var keys = new List<SecurityKey>
        {
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey)),
        };
        if (!string.IsNullOrWhiteSpace(PreviousSecretKey))
        {
            keys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(PreviousSecretKey)));
        }
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKeys = keys,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
            ClockSkew = DefaultClockSkew,
        };
    }
}

public sealed class JwtTokenService : ITokenService
{
    private readonly JwtTokenServiceOptions _options;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly JwtSecurityTokenHandler _handler = new();

    public JwtTokenService(JwtTokenServiceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SecretKey) || Encoding.UTF8.GetByteCount(options.SecretKey) < 32)
        {
            throw new ArgumentException("JWT secret key must be at least 32 bytes.", nameof(options));
        }
        _options = options;
        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SecretKey));
    }

    public AccessTokenDto GenerateAccessToken(
        User user,
        TenantType tenantType,
        IReadOnlyCollection<string> roleNames,
        Guid sessionId,
        ImpersonationClaim? impersonation = null,
        IReadOnlyDictionary<string, Guid>? profileIds = null,
        Guid? derivedFromSessionId = null,
        TimeSpan? overrideLifetime = null,
        string? avatarEmoji = null,
        string? avatarBgColor = null)
    {
        var now = DateTime.UtcNow;
        var expires = overrideLifetime is { } life
            ? now.Add(life)
            : now.AddMinutes(_options.AccessTokenMinutes);

        // Add-child redesign: scope distinguishes parent surfaces
        // (billing, dashboards) from child surfaces (student lessons).
        // RequireScope filter consults this claim.
        var scope = user.AccountType == Muallimi.Domain.Identity.Enums.AccountType.Managed
            ? "child"
            : "parent";

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
            new("name", user.FullName ?? string.Empty),
            new("tenant_id", user.TenantId.ToString("D")),
            new("tenant_type", tenantType.ToString().ToLowerInvariant()),
            new("locale", user.Locale),
            new("session_id", sessionId.ToString("D")),
            new("scope", scope),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (derivedFromSessionId is { } derivedId)
        {
            claims.Add(new Claim("derived_from_session_id", derivedId.ToString("D")));
        }
        // Add-child redesign: child accounts carry their visual identity
        // (the emoji + background colour the parent picked) as JWT claims
        // so the topbar can render the actual avatar — visually different
        // from a parent session — without an extra fetch.
        if (!string.IsNullOrWhiteSpace(avatarEmoji))
        {
            claims.Add(new Claim("avatar_emoji", avatarEmoji));
        }
        if (!string.IsNullOrWhiteSpace(avatarBgColor))
        {
            claims.Add(new Claim("avatar_bg_color", avatarBgColor));
        }
        // Phase 9 credential-management: expose the credential tier on the
        // token so the per-tier child settings page can render the right
        // surface (no credential UI for profile_switch_only, read-only PIN
        // notice for pin, change-password form for username_password)
        // without an extra round-trip. Personal accounts have null
        // LoginMethod and the claim is omitted.
        if (!string.IsNullOrWhiteSpace(user.LoginMethod))
        {
            claims.Add(new Claim("login_method", user.LoginMethod));
        }
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        }
        claims.Add(new Claim("email_verified", user.EmailVerified ? "true" : "false"));
        foreach (var role in roleNames)
        {
            claims.Add(new Claim("roles", role));
        }

        // T155 — impersonating claim: null → empty string (no impersonation);
        // non-null → JSON object per identity.claims contract.
        if (impersonation is not null)
        {
            var json = JsonSerializer.Serialize(new
            {
                by = impersonation.By,
                session = impersonation.Session,
                expires_at = impersonation.ExpiresAt.ToString("O"),
            });
            claims.Add(new Claim("impersonating", json, JsonClaimValueTypes.Json));
        }
        else
        {
            claims.Add(new Claim("impersonating", string.Empty));
        }

        // profile_ids — generalized 1:1 domain profile resolution.
        // Always emitted so consumers can index without a null check;
        // empty object when the user has no domain profile.
        var profileIdsJson = SerializeProfileIds(profileIds);
        claims.Add(new Claim("profile_ids", profileIdsJson, JsonClaimValueTypes.Json));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

        return new AccessTokenDto(_handler.WriteToken(token), expires);
    }

    public (string token, string hash) GenerateRefreshToken()
    {
        // 256-bit cryptographically-random token, base64url-encoded.
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncoder.Encode(raw);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        return (token, hash);
    }

    private static string SerializeProfileIds(IReadOnlyDictionary<string, Guid>? profileIds)
    {
        if (profileIds is null || profileIds.Count == 0)
        {
            return "{}";
        }
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in profileIds)
        {
            ordered[kv.Key] = kv.Value.ToString("D");
        }
        return JsonSerializer.Serialize(ordered);
    }

    public ClaimsPrincipal? ValidateAccessToken(string token)
    {
        try
        {
            var principal = _handler.ValidateToken(token, _options.CreateValidationParameters(), out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }
}
