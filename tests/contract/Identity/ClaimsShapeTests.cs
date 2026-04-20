using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity;

/// <summary>
/// T055 — Contract test for the <c>identity.claims</c> contract.
///
/// Asserts every claim listed in
/// <c>specs/009-identity-auth/contracts/identity-claims-contract.md</c>
/// is present on every issued access token with the correct type. The
/// frontend (<c>auth.service.ts</c>) and the two consumer middleware
/// implementations (<c>muallimi-ai-service</c>, <c>muallimi-document-ingestion</c>)
/// depend on this exact claim shape.
///
/// Frozen claim names: sub, email (when Personal), name, tenant_id,
/// tenant_type, roles, locale, session_id, impersonating, jti, iat, exp,
/// iss, aud.
/// </summary>
public class ClaimsShapeTests
{
    private const string SecretKey = "unit-test-secret-key-32-bytes-min-ok!!";

    private static JwtTokenService CreateService() => new(new JwtTokenServiceOptions
    {
        SecretKey = SecretKey,
        Issuer = "muallimi-main-backend",
        Audience = "muallimi-platform",
        AccessTokenMinutes = 15,
    });

    private static User BuildPersonalUser() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        AccountType = AccountType.Personal,
        Email = "ahmed@example.com",
        NormalizedEmail = "ahmed@example.com",
        EmailVerified = true,
        FullName = "أحمد محمد",
        Locale = "ar",
        Status = UserStatus.Active,
    };

    private static User BuildManagedUser() => new()
    {
        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        AccountType = AccountType.Managed,
        Username = "student01",
        NormalizedUsername = "student01",
        ManagedByUserId = Guid.NewGuid(),
        FullName = "طالب",
        Locale = "ar",
        Status = UserStatus.Active,
    };

    private static JwtSecurityToken DecodeUnvalidated(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token);

    [Fact]
    public void Personal_Access_Token_Contains_All_Required_Claims()
    {
        var user = BuildPersonalUser();
        var sessionId = Guid.NewGuid();
        var issued = CreateService().GenerateAccessToken(
            user,
            TenantType.Family,
            new[] { "parent" },
            sessionId);

        var jwt = DecodeUnvalidated(issued.Token);

        // Issuer / audience / times
        Assert.Equal("muallimi-main-backend", jwt.Issuer);
        Assert.Contains("muallimi-platform", jwt.Audiences);
        Assert.True(jwt.ValidTo > jwt.ValidFrom);

        // sub
        var sub = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value;
        Assert.Equal(user.Id.ToString("D"), sub);

        // email (Personal required)
        var email = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Email).Value;
        Assert.Equal(user.Email, email);

        // name
        Assert.Equal(user.FullName, jwt.Claims.Single(c => c.Type == "name").Value);

        // tenant_id (string UUID)
        var tenantRaw = jwt.Claims.Single(c => c.Type == "tenant_id").Value;
        Assert.True(Guid.TryParse(tenantRaw, out var parsedTenant));
        Assert.Equal(user.TenantId, parsedTenant);

        // tenant_type lowercased
        Assert.Equal("family", jwt.Claims.Single(c => c.Type == "tenant_type").Value);

        // locale
        Assert.Equal("ar", jwt.Claims.Single(c => c.Type == "locale").Value);

        // session_id
        var sessionRaw = jwt.Claims.Single(c => c.Type == "session_id").Value;
        Assert.True(Guid.TryParse(sessionRaw, out var parsedSession));
        Assert.Equal(sessionId, parsedSession);

        // jti
        var jti = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        Assert.True(!string.IsNullOrWhiteSpace(jti));

        // roles — may repeat as an array
        var roles = jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToArray();
        Assert.Contains("parent", roles);

        // iat and exp populate ValidFrom / ValidTo on the decoded token.
        Assert.NotEqual(default, jwt.ValidFrom);
        Assert.NotEqual(default, jwt.ValidTo);
        Assert.True(jwt.ValidTo > jwt.ValidFrom);
    }

    [Fact]
    public void Managed_Account_Token_Omits_Email_Claim()
    {
        var user = BuildManagedUser();
        var issued = CreateService().GenerateAccessToken(
            user,
            TenantType.Family,
            new[] { "student" },
            Guid.NewGuid());

        var jwt = DecodeUnvalidated(issued.Token);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == JwtRegisteredClaimNames.Email);
        Assert.Equal("family", jwt.Claims.Single(c => c.Type == "tenant_type").Value);
    }

    [Fact]
    public void Platform_Token_Emits_Multiple_Roles_As_Array()
    {
        var user = BuildPersonalUser();
        user.TenantId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var issued = CreateService().GenerateAccessToken(
            user,
            TenantType.Platform,
            new[] { "super-admin", "curriculum-admin" },
            Guid.NewGuid());

        var jwt = DecodeUnvalidated(issued.Token);
        Assert.Equal("platform", jwt.Claims.Single(c => c.Type == "tenant_type").Value);

        var roles = jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToArray();
        Assert.Equal(2, roles.Length);
        Assert.Contains("super-admin", roles);
        Assert.Contains("curriculum-admin", roles);
    }

    [Fact]
    public void Impersonation_Token_Carries_Impersonating_Claim()
    {
        var target = BuildPersonalUser();
        var adminId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var issued = CreateService().GenerateAccessToken(
            target,
            TenantType.Family,
            new[] { "parent" },
            Guid.NewGuid(),
            impersonation: new ImpersonationClaim(
                By: adminId.ToString("D"),
                Session: Guid.NewGuid().ToString("D"),
                ExpiresAt: DateTime.UtcNow.AddHours(1)));

        var jwt = DecodeUnvalidated(issued.Token);
        var impersonating = jwt.Claims.Single(c => c.Type == "impersonating").Value;
        // The claim is a JSON object; it must contain the impersonator's ID in the "by" field.
        Assert.Contains(adminId.ToString("D"), impersonating);

        // Target user's sub is still the authenticated subject.
        Assert.Equal(target.Id.ToString("D"),
            jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
    }

    [Fact]
    public void ValidateAccessToken_Accepts_Freshly_Issued_Token()
    {
        var svc = CreateService();
        var issued = svc.GenerateAccessToken(
            BuildPersonalUser(),
            TenantType.Family,
            new[] { "parent" },
            Guid.NewGuid());

        var principal = svc.ValidateAccessToken(issued.Token);
        Assert.NotNull(principal);
    }

    [Fact]
    public void ValidateAccessToken_Rejects_Token_Signed_With_Different_Key()
    {
        var issuerA = CreateService();
        var issued = issuerA.GenerateAccessToken(
            BuildPersonalUser(),
            TenantType.Family,
            new[] { "parent" },
            Guid.NewGuid());

        var issuerB = new JwtTokenService(new JwtTokenServiceOptions
        {
            SecretKey = "different-secret-key-32-bytes-min-ok!!",
            Issuer = "muallimi-main-backend",
            Audience = "muallimi-platform",
        });
        Assert.Null(issuerB.ValidateAccessToken(issued.Token));
    }
}
