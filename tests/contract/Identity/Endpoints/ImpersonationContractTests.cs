using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T151 — Contract tests covering:
///   • POST /admin/impersonate route constants
///   • POST /admin/impersonate/end route constants
///   • <see cref="ImpersonateRequest"/> request shape
///   • <see cref="EndImpersonationRequest"/> request shape
///   • <see cref="ImpersonationStartedResponse"/> response shape
///   • JWT <c>impersonating</c> claim carries <c>by</c>, <c>session</c>,
///     <c>expires_at</c> as a JSON object — not a bare string
/// </summary>
public class ImpersonationContractTests
{
    // ── Route constants ─────────────────────────────────────────────────

    [Fact]
    public void StartRoute_Constant_Is_Pinned()
    {
        Assert.Equal("/impersonate", AdminImpersonationEndpoints.StartRoute);
    }

    [Fact]
    public void EndRoute_Constant_Is_Pinned()
    {
        Assert.Equal("/impersonate/end", AdminImpersonationEndpoints.EndRoute);
    }

    // ── Request shape ───────────────────────────────────────────────────

    [Fact]
    public void ImpersonateRequest_Has_TargetUserId_And_Reason()
    {
        var names = JsonNames(typeof(ImpersonateRequest));
        Assert.Contains("targetUserId", names);
        Assert.Contains("reason", names);
    }

    [Fact]
    public void EndImpersonationRequest_Has_ImpersonationSessionId()
    {
        var names = JsonNames(typeof(EndImpersonationRequest));
        Assert.Contains("impersonationSessionId", names);
    }

    // ── Response shape ──────────────────────────────────────────────────

    [Fact]
    public void ImpersonationStartedResponse_Shape_Is_Pinned()
    {
        var names = JsonNames(typeof(ImpersonationStartedResponse));
        Assert.Contains("impersonationSessionId", names);
        Assert.Contains("accessToken", names);
        Assert.Contains("expiresIn", names);
        Assert.Contains("targetUserId", names);
        Assert.Contains("targetFullName", names);
        Assert.Contains("expiresAt", names);
    }

    // ── JWT impersonating claim shape ────────────────────────────────────

    [Fact]
    public async Task Impersonation_Token_Carries_Object_Claim_With_By_Session_ExpiresAt()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (superAdminId, platformTenantId) = await SetupSuperAdminAsync(h);
        var (targetId, _) = await RegisterAndVerifyParentAsync(h, "target-contract@example.com");

        var outcome = await h.ImpersonationService.StartAsync(new StartImpersonationCommand(
            ActorUserId: superAdminId,
            ActorTenantId: platformTenantId,
            TargetUserId: targetId,
            Reason: "contract test",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(outcome.Success, outcome.Message);
        var payload = outcome.Payload!;
        Assert.False(string.IsNullOrWhiteSpace(payload.AccessToken));

        // Validate the token and inspect the impersonating claim.
        var principal = h.Tokens.ValidateAccessToken(payload.AccessToken);
        Assert.NotNull(principal);

        var impersonatingRaw = principal!.FindFirst("impersonating")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(impersonatingRaw),
            "impersonating claim must be present and non-empty on impersonation tokens");

        // Must be valid JSON with the three required fields.
        using var doc = JsonDocument.Parse(impersonatingRaw!);
        Assert.True(doc.RootElement.TryGetProperty("by", out var by),
            "impersonating claim must have 'by'");
        Assert.True(doc.RootElement.TryGetProperty("session", out var session),
            "impersonating claim must have 'session'");
        Assert.True(doc.RootElement.TryGetProperty("expires_at", out var expiresAt),
            "impersonating claim must have 'expires_at'");

        Assert.Equal(superAdminId.ToString("D"), by.GetString());
        Assert.Equal(payload.ImpersonationSessionId, session.GetString());
        Assert.False(string.IsNullOrWhiteSpace(expiresAt.GetString()));

        // Target user's sub claim matches targetId (JWT handler may map sub → NameIdentifier).
        var sub = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Assert.Equal(targetId.ToString("D"), sub);
    }

    [Fact]
    public async Task Normal_Token_Has_Empty_Impersonating_Claim()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        await RegisterAndVerifyAsync(h, "normal@example.com", "HorseBatteryStaple!77");

        var login = await h.AuthService.LoginAsync(new LoginCommand(
            "normal@example.com", "HorseBatteryStaple!77",
            RememberMe: false, TwoFactorCode: null, TempToken: null,
            IpAddress: "127.0.0.1", UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D")));

        Assert.True(login.Success);
        var principal = h.Tokens.ValidateAccessToken(login.Payload!.AccessToken);
        Assert.NotNull(principal);

        var impersonating = principal!.FindFirst("impersonating")?.Value;
        // Normal tokens have the claim but it should be empty.
        Assert.True(impersonating is null || impersonating == string.Empty,
            "Non-impersonation tokens must have no impersonating payload");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static IEnumerable<string> JsonNames(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p =>
            {
                var attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                return attr?.Name ?? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(p.Name);
            });

    private static async Task RegisterAndVerifyAsync(IdentityTestHarness h, string email, string password)
    {
        var corr = Guid.NewGuid().ToString("D");
        await h.AuthService.RegisterParentAsync(new RegisterParentCommand(
            email, password, "تجربة", "Test", "ar", true,
            "127.0.0.1", "xunit", corr));
        var record = h.Notifications.Dispatched[^1];
        var plaintext = Uri.UnescapeDataString(record.Link.Split("token=", 2)[1]);
        await h.Verification.ConsumeAsync(plaintext, corr);
    }

    private static async Task<(Guid userId, Guid tenantId)> RegisterAndVerifyParentAsync(
        IdentityTestHarness h, string email)
    {
        await RegisterAndVerifyAsync(h, email, "HorseBatteryStaple!77");
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .FirstAsync(u => u.NormalizedEmail == email.Trim().ToLowerInvariant());
        return (user.Id, user.TenantId);
    }

    private static async Task<(Guid superAdminId, Guid platformTenantId)> SetupSuperAdminAsync(
        IdentityTestHarness h)
    {
        var platformTenant = await h.Db.IdentityTenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Type == TenantType.Platform);
        var superAdminRole = await h.Db.IdentityRoles.IgnoreQueryFilters()
            .FirstAsync(r => r.Name == "super-admin");

        var superAdmin = new Muallimi.Domain.Identity.Entities.User
        {
            Id = Guid.NewGuid(),
            TenantId = platformTenant.Id,
            Email = "superadmin-contract@platform.io",
            NormalizedEmail = "SUPERADMIN-CONTRACT@PLATFORM.IO",
            FullName = "Super Admin",
            Locale = "ar",
            Status = Muallimi.Domain.Identity.Enums.UserStatus.Active,
            AccountType = Muallimi.Domain.Identity.Enums.AccountType.Personal,
            PasswordHash = h.Passwords.Hash("SuperAdmin!77"),
            EmailVerified = true,
            CreatedAt = DateTime.UtcNow,
        };
        h.Db.IdentityUsers.Add(superAdmin);
        h.Db.IdentityUserRoles.Add(new Muallimi.Domain.Identity.Entities.UserRole
        {
            Id = Guid.NewGuid(),
            UserId = superAdmin.Id,
            RoleId = superAdminRole.Id,
            TenantId = platformTenant.Id,
            GrantedAt = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();

        return (superAdmin.Id, platformTenant.Id);
    }
}
