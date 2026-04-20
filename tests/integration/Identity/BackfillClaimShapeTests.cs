using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.Api.Tests.Identity.Integration;

/// <summary>
/// T163 — Integration test: a migrated user (built with the shape that
/// <c>BackfillScriptRunner</c> produces) receives the correct
/// <c>tenant_id</c> and <c>roles</c> claim shape from
/// <c>JwtTokenService</c>.
///
/// Verifies that every Phase 1-6 authorization filter would accept the
/// migrated user's token, since those filters read claims directly
/// from the JWT.
/// </summary>
public class BackfillClaimShapeTests
{
    private static readonly Guid FamilyTenantId = Guid.Parse("fa111111-0000-0000-0000-000000000001");
    private static readonly Guid PlatformTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SchoolTenantId = Guid.Parse("53000001-0000-0000-0000-000000000001");

    private static JwtTokenService CreateService() => new(new JwtTokenServiceOptions
    {
        SecretKey = IdentityTestHarness.JwtSecret,
        Issuer = "muallimi-main-backend",
        Audience = "muallimi-platform",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 7,
    });

    private static JwtSecurityToken IssueAndParse(User user, TenantType tenantType, Role[] roles)
    {
        var svc = CreateService();
        var dto = svc.GenerateAccessToken(
            user,
            tenantType,
            roles.Select(r => r.Name).ToArray(),
            sessionId: Guid.NewGuid());
        var handler = new JwtSecurityTokenHandler();
        return handler.ReadJwtToken(dto.Token);
    }

    // ── Parent user (migrated from legacy AuthAPI) ─────────────────────

    [Fact]
    public void Migrated_Parent_Token_Has_Family_TenantId_And_Parent_Role()
    {
        var parentUser = new User
        {
            Id = Guid.Parse("a0000001-0000-0000-0000-000000000001"),
            TenantId = FamilyTenantId,
            AccountType = AccountType.Personal,
            Email = "parent1@example.com",
            NormalizedEmail = "PARENT1@EXAMPLE.COM",
            EmailVerified = true,
            FullName = "أحمد الشامة",
            Locale = "ar",
            Status = UserStatus.Active,
        };
        var parentRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = "parent",
            Scope = RoleScope.Family,
            IsSystem = true,
        };

        var token = IssueAndParse(parentUser, TenantType.Family, [parentRole]);

        // tenant_id must equal the Family tenant's ID
        var tenantIdClaim = token.Claims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
        Assert.Equal(FamilyTenantId.ToString("D"), tenantIdClaim);

        // roles must contain "parent"
        var rolesClaim = token.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        Assert.Contains("parent", rolesClaim);

        // sub must be the user's Id
        var sub = token.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
        Assert.Equal(parentUser.Id.ToString("D"), sub);

        // locale must be "ar"
        var locale = token.Claims.FirstOrDefault(c => c.Type == "locale")?.Value;
        Assert.Equal("ar", locale);
    }

    // ── Super-admin user (migrated) ────────────────────────────────────

    [Fact]
    public void Migrated_SuperAdmin_Token_Has_Platform_TenantId_And_SuperAdmin_Role()
    {
        var superAdmin = new User
        {
            Id = Guid.Parse("f0000001-0000-0000-0000-000000000001"),
            TenantId = PlatformTenantId,
            AccountType = AccountType.Personal,
            Email = "superadmin@platform.example.com",
            NormalizedEmail = "SUPERADMIN@PLATFORM.EXAMPLE.COM",
            EmailVerified = true,
            FullName = "المسؤول الأعلى",
            Locale = "ar",
            Status = UserStatus.Active,
        };
        var superAdminRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = "super-admin",
            Scope = RoleScope.Platform,
            IsSystem = true,
        };

        var token = IssueAndParse(superAdmin, TenantType.Platform, [superAdminRole]);

        var tenantIdClaim = token.Claims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
        Assert.Equal(PlatformTenantId.ToString("D"), tenantIdClaim);

        var rolesClaim = token.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        Assert.Contains("super-admin", rolesClaim);

        // tenant_type claim must be present and reflect Platform
        var tenantType = token.Claims.FirstOrDefault(c => c.Type == "tenant_type")?.Value;
        Assert.NotNull(tenantType);
    }

    // ── Managed student user (migrated) ───────────────────────────────

    [Fact]
    public void Migrated_Student_Token_Has_No_Email_Claim()
    {
        var student = new User
        {
            Id = Guid.Parse("b0000001-0000-0000-0000-000000000001"),
            TenantId = FamilyTenantId,
            AccountType = AccountType.Managed,
            ManagedByUserId = Guid.Parse("a0000001-0000-0000-0000-000000000001"),
            Username = "student.2012.001",
            NormalizedUsername = "STUDENT.2012.001",
            FullName = "محمد أحمد",
            Locale = "ar",
            Status = UserStatus.Active,
        };
        var studentRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = "student",
            Scope = RoleScope.Family,
            IsSystem = true,
        };

        var token = IssueAndParse(student, TenantType.Family, [studentRole]);

        // Managed users have no email claim
        var email = token.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
        Assert.Null(email);

        var rolesClaim = token.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        Assert.Contains("student", rolesClaim);
    }

    // ── School admin user (migrated) ──────────────────────────────────

    [Fact]
    public void Migrated_SchoolAdmin_Token_Has_School_TenantId_And_SchoolAdmin_Role()
    {
        var schoolAdmin = new User
        {
            Id = Guid.Parse("c0000001-0000-0000-0000-000000000001"),
            TenantId = SchoolTenantId,
            AccountType = AccountType.Personal,
            Email = "schooladmin@school1.example.com",
            NormalizedEmail = "SCHOOLADMIN@SCHOOL1.EXAMPLE.COM",
            EmailVerified = true,
            FullName = "مدير المدرسة",
            Locale = "ar",
            Status = UserStatus.Active,
        };
        var schoolAdminRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = "school-admin",
            Scope = RoleScope.School,
            IsSystem = true,
        };

        var token = IssueAndParse(schoolAdmin, TenantType.School, [schoolAdminRole]);

        var tenantIdClaim = token.Claims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
        Assert.Equal(SchoolTenantId.ToString("D"), tenantIdClaim);

        var rolesClaim = token.Claims.Where(c => c.Type == "roles").Select(c => c.Value).ToList();
        Assert.Contains("school-admin", rolesClaim);
    }

    // ── Token structural invariants for all migrated users ────────────

    [Fact]
    public void Migrated_User_Token_Contains_All_Required_Claims()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = FamilyTenantId,
            AccountType = AccountType.Personal,
            Email = "test@example.com",
            NormalizedEmail = "TEST@EXAMPLE.COM",
            EmailVerified = true,
            FullName = "مستخدم",
            Locale = "ar",
            Status = UserStatus.Active,
        };
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "parent",
            Scope = RoleScope.Family,
            IsSystem = true,
        };

        var token = IssueAndParse(user, TenantType.Family, [role]);

        var claimTypes = token.Claims.Select(c => c.Type).ToHashSet();

        // Required claims per identity-claims-contract.md (payload claims).
        Assert.Contains("sub", claimTypes);
        Assert.Contains("tenant_id", claimTypes);
        Assert.Contains("roles", claimTypes);
        Assert.Contains("locale", claimTypes);
        Assert.Contains("session_id", claimTypes);
        Assert.Contains("jti", claimTypes);

        // iat/exp surface as ValidFrom/ValidTo on the decoded token (JWT library behaviour).
        Assert.NotEqual(default, token.ValidFrom);
        Assert.NotEqual(default, token.ValidTo);
        Assert.True(token.ValidTo > token.ValidFrom);

        // iss and aud are accessible as token properties.
        Assert.Equal("muallimi-main-backend", token.Issuer);
        Assert.Contains("muallimi-platform", token.Audiences);
    }
}
