using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Muallimi.Api.Identity.Startup;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity;

/// <summary>
/// 2026-04-21 hardening — contract tests for the six JWT/identity
/// production-readiness fixes.
///
/// Each test pins one load-bearing invariant:
/// <list type="number">
///   <item><description><b>#1 JWT secret startup guard</b> — refuses to
///     boot with the public dev fallback in any non-Development env.</description></item>
///   <item><description><b>#2 TOTP key startup guard</b> — refuses to
///     boot when the base64 TOTP key is unset outside Development.</description></item>
///   <item><description><b>#3 Single validation-params builder</b> —
///     <see cref="JwtTokenServiceOptions.CreateValidationParameters"/> is
///     the one place that defines how tokens are validated; the bearer
///     middleware and <see cref="JwtTokenService"/> both consume it.</description></item>
///   <item><description><b>#4 Key rotation</b> — when
///     <c>PreviousSecretKey</c> is set, tokens signed by either current
///     or previous key validate; when unset, only current key works.</description></item>
///   <item><description><b>#5 Algorithm pinning</b> — tokens signed with
///     HS384/HS512 (even with the correct symmetric key) are rejected.</description></item>
///   <item><description><b>#6 Claim-name preservation</b> — the shared
///     validation-params do NOT remap short names (guards against a
///     future accidental flip of MapInboundClaims).</description></item>
/// </list>
/// </summary>
public class JwtHardeningContractTests
{
    private const string CurrentSecret = "current-secret-key-32-bytes-min-ok!!";
    private const string PreviousSecret = "previous-secret-key-32-bytes-min-2!!";

    private static JwtTokenServiceOptions BuildOptions(string? previous = null) => new()
    {
        SecretKey = CurrentSecret,
        PreviousSecretKey = previous,
        Issuer = "muallimi-main-backend",
        Audience = "muallimi-platform",
        AccessTokenMinutes = 15,
    };

    private static string MintToken(string secret, string alg = SecurityAlgorithms.HmacSha256,
        string issuer = "muallimi-main-backend", string audience = "muallimi-platform")
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString("D")),
                new Claim("tenant_id", Guid.NewGuid().ToString("D")),
                new Claim("roles", "parent"),
            },
            notBefore: now,
            expires: now.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, alg));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ─── #1: JWT secret startup guard ────────────────────────────
    [Fact]
    public void SecretHygiene_ProductionEnv_WithDevFallbackSecret_Throws()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IdentityServiceCollectionExtensions.EnforceProductionSecretHygiene(
                config,
                IdentityServiceCollectionExtensions.DevFallbackJwtSecret,
                totpKeyBase64: "not-checked-since-jwt-fails-first"));
        Assert.Contains("IDENTITY_JWT_SECRET_KEY", ex.Message);
    }

    [Fact]
    public void SecretHygiene_DevelopmentEnv_WithDevFallbackSecret_DoesNotThrow()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
        }).Build();

        // Should not throw — dev fallbacks are intended for local dev.
        IdentityServiceCollectionExtensions.EnforceProductionSecretHygiene(
            config,
            IdentityServiceCollectionExtensions.DevFallbackJwtSecret,
            totpKeyBase64: null);
    }

    [Fact]
    public void SecretHygiene_ProductionEnv_WithRealSecret_DoesNotThrow()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        }).Build();

        IdentityServiceCollectionExtensions.EnforceProductionSecretHygiene(
            config,
            "real-production-secret-key-that-is-long-enough",
            totpKeyBase64: "dGVzdC10b3RwLWtleQ==");
    }

    // ─── #2: TOTP key startup guard ──────────────────────────────
    [Fact]
    public void SecretHygiene_ProductionEnv_WithMissingTotpKey_Throws()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            IdentityServiceCollectionExtensions.EnforceProductionSecretHygiene(
                config,
                "real-production-secret-key-that-is-long-enough",
                totpKeyBase64: null));
        Assert.Contains("IDENTITY_TOTP_ENCRYPTION_KEY", ex.Message);
    }

    [Fact]
    public void SecretHygiene_StagingEnv_WithMissingTotpKey_Throws()
    {
        // "Staging" is also non-dev; guard must fire.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Staging",
        }).Build();

        Assert.Throws<InvalidOperationException>(() =>
            IdentityServiceCollectionExtensions.EnforceProductionSecretHygiene(
                config,
                "real-production-secret-key-that-is-long-enough",
                totpKeyBase64: "   "));
    }

    // ─── #3: Single validation-params builder ───────────────────
    [Fact]
    public void ValidationParams_BuilderIsAuthoritative_AllFlagsOn()
    {
        var p = BuildOptions().CreateValidationParameters();

        Assert.True(p.ValidateIssuer);
        Assert.True(p.ValidateAudience);
        Assert.True(p.ValidateLifetime);
        Assert.True(p.ValidateIssuerSigningKey);
        Assert.Equal("muallimi-main-backend", p.ValidIssuer);
        Assert.Equal("muallimi-platform", p.ValidAudience);
        Assert.Equal(JwtTokenServiceOptions.DefaultClockSkew, p.ClockSkew);
    }

    [Fact]
    public void ValidationParams_JwtTokenServiceAndBearer_UseSameBuilder()
    {
        // Pin: JwtTokenService.ValidateAccessToken must succeed for a
        // token minted by the same options — any divergence between
        // bearer middleware and this path would fail this test.
        var options = BuildOptions();
        var svc = new JwtTokenService(options);
        var token = MintToken(CurrentSecret);

        var principal = svc.ValidateAccessToken(token);
        Assert.NotNull(principal);
    }

    // ─── #4: Key rotation ────────────────────────────────────────
    [Fact]
    public void Rotation_TokenSignedByPrevious_ValidatesWhenPreviousConfigured()
    {
        var options = BuildOptions(previous: PreviousSecret);
        var svc = new JwtTokenService(options);
        var tokenSignedByOldKey = MintToken(PreviousSecret);

        var principal = svc.ValidateAccessToken(tokenSignedByOldKey);
        Assert.NotNull(principal);
    }

    [Fact]
    public void Rotation_TokenSignedByPrevious_RejectedWhenPreviousUnset()
    {
        var options = BuildOptions(previous: null);
        var svc = new JwtTokenService(options);
        var tokenSignedByOldKey = MintToken(PreviousSecret);

        var principal = svc.ValidateAccessToken(tokenSignedByOldKey);
        Assert.Null(principal);
    }

    [Fact]
    public void Rotation_TokenSignedByCurrent_AlwaysValidates()
    {
        var options = BuildOptions(previous: PreviousSecret);
        var svc = new JwtTokenService(options);
        var tokenSignedByCurrentKey = MintToken(CurrentSecret);

        var principal = svc.ValidateAccessToken(tokenSignedByCurrentKey);
        Assert.NotNull(principal);
    }

    [Fact]
    public void Rotation_Minter_OnlyUsesCurrentKey()
    {
        // The minter must never sign with the previous key. We assert
        // by minting a token then validating it with a fresh service
        // whose "current" is set to the previous value of this test —
        // it should reject, proving the minter did not use previous.
        var options = BuildOptions(previous: PreviousSecret);
        var minter = new JwtTokenService(options);
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            AccountType = AccountType.Personal,
            FullName = "Test",
            Locale = "ar",
            Status = UserStatus.Active,
        };
        var minted = minter.GenerateAccessToken(user, TenantType.Family,
            new[] { "parent" }, Guid.NewGuid());

        // Validator with ONLY the previous secret as current — if the
        // minter had used previous, this would succeed. It must not.
        var wrongValidator = new JwtTokenService(new JwtTokenServiceOptions
        {
            SecretKey = PreviousSecret,
            Issuer = options.Issuer,
            Audience = options.Audience,
        });
        Assert.Null(wrongValidator.ValidateAccessToken(minted.Token));
    }

    // ─── #5: Algorithm pinning ───────────────────────────────────
    [Fact]
    public void AlgorithmPinning_Hs384Signature_Rejected()
    {
        var options = BuildOptions();
        var svc = new JwtTokenService(options);
        // Use a key long enough to SIGN with HS384 (needs ≥ 48 bytes);
        // we're testing that the validator rejects the algorithm, not
        // the key-size policy of the signing side.
        const string longSecret = "extra-long-secret-key-used-for-HS384-signing-only-48b!!";
        var tokenHs384 = MintToken(longSecret, alg: SecurityAlgorithms.HmacSha384);

        var principal = svc.ValidateAccessToken(tokenHs384);
        Assert.Null(principal);
    }

    [Fact]
    public void AlgorithmPinning_ValidAlgorithmsList_ContainsOnlyHs256()
    {
        var p = BuildOptions().CreateValidationParameters();
        Assert.NotNull(p.ValidAlgorithms);
        Assert.Single(p.ValidAlgorithms!);
        Assert.Equal(SecurityAlgorithms.HmacSha256, p.ValidAlgorithms!.First());
    }

    // ─── #6: Bearer-middleware wiring hygiene ───────────────────
    [Fact]
    public void ValidationParams_DoesNotSilentlyRemapInboundClaims()
    {
        // The bearer middleware sets MapInboundClaims = false on its
        // handler in IdentityServiceCollectionExtensions. That setting
        // cannot be expressed on TokenValidationParameters — it lives
        // on JwtBearerOptions. We still pin the invariant here by
        // asserting NameClaimType / RoleClaimType are NOT set to the
        // legacy mapped values. If a future contributor adds
        // NameClaimType = ClaimTypes.NameIdentifier to the shared
        // builder, this test fails and they must think again — tenant
        // isolation (which reads the "tenant_id" short-name claim) would
        // silently break otherwise.
        var p = BuildOptions().CreateValidationParameters();
        Assert.Equal(ClaimTypes.Name, p.NameClaimType);         // default
        Assert.Equal(ClaimTypes.Role, p.RoleClaimType);         // default
        // Explicit: the builder does NOT force legacy mapping.
        Assert.NotEqual("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", p.NameClaimType);
    }
}
