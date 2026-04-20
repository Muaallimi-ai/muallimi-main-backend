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
        ImpersonationClaim? impersonation = null);

    (string token, string hash) GenerateRefreshToken();

    ClaimsPrincipal? ValidateAccessToken(string token);
}

public sealed record AccessTokenDto(string Token, DateTime ExpiresAt);

public sealed class JwtTokenServiceOptions
{
    public string SecretKey { get; init; } = string.Empty;
    public string Issuer { get; init; } = "muallimi-main-backend";
    public string Audience { get; init; } = "muallimi-platform";
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 7;
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
        ImpersonationClaim? impersonation = null)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
            new("name", user.FullName ?? string.Empty),
            new("tenant_id", user.TenantId.ToString("D")),
            new("tenant_type", tenantType.ToString().ToLowerInvariant()),
            new("locale", user.Locale),
            new("session_id", sessionId.ToString("D")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email));
        }
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

    public ClaimsPrincipal? ValidateAccessToken(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKey = _signingKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        try
        {
            var principal = _handler.ValidateToken(token, parameters, out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }
}
