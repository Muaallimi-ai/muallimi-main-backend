using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Identity.Startup;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Validators;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T062 — Contract test for <c>POST /api/auth/register</c> and
/// <c>/api/auth/register/parent</c>. Pins:
///   • Route constants exposed by <see cref="PublicAuthEndpoints"/>
///     (used by the frontend + smoke scripts).
///   • Wire shape of <see cref="RegisterRequest"/> (camelCase casing,
///     required field presence).
///   • Wire shape of <see cref="AuthResponse"/>.
///   • End-to-end call against <see cref="Muallimi.Api.Identity.Services.IAuthService"/>:
///       — creates Family tenant + User + parent grant in one unit of work;
///       — issues a verification token (captured by the in-memory
///         notification spy) without auto-logging-in the user;
///       — emits the <c>register_parent</c> audit event.
/// </summary>
public class RegisterParentContractTests
{
    [Fact]
    public void Route_Constants_Are_Pinned()
    {
        Assert.Equal("/register", PublicAuthEndpoints.RegisterRoute);
        Assert.Equal("/register/parent", PublicAuthEndpoints.RegisterParentRoute);
        Assert.Equal("/register/school-admin", PublicAuthEndpoints.RegisterSchoolAdminRoute);
        Assert.Equal("/api/auth", IdentityEndpointRouteBuilderExtensions.IdentityRoutePrefix);
    }

    [Fact]
    public void Request_Body_Shape_Is_CamelCase()
    {
        var names = JsonNames(typeof(RegisterRequest));
        Assert.Contains("email", names);
        Assert.Contains("password", names);
        Assert.Contains("fullName", names);
        Assert.Contains("fullNameEn", names);
        Assert.Contains("locale", names);
        Assert.Contains("acceptedTerms", names);
    }

    [Fact]
    public void SchoolAdmin_Request_Body_Carries_SchoolDisplayName()
    {
        var names = JsonNames(typeof(SchoolAdminRegisterRequest));
        Assert.Contains("email", names);
        Assert.Contains("password", names);
        Assert.Contains("schoolDisplayName", names);
        Assert.Contains("acceptedTerms", names);
    }

    [Fact]
    public void AuthResponse_Carries_Every_Contract_Field()
    {
        var names = JsonNames(typeof(AuthResponse));
        foreach (var expected in new[]
        {
            "accessToken", "refreshToken", "expiresIn",
            "userId", "email", "fullName", "fullNameEn",
            "tenantId", "tenantType", "roles", "locale",
            "emailVerified", "twoFactorEnabled", "requiresPasswordReset",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public async Task RegisterParent_Creates_Family_Tenant_User_And_Verification_Email()
    {
        using var h = await IdentityTestHarness.CreateAsync();

        var cmd = new RegisterParentCommand(
            Email: "parent@example.com",
            Password: "HorseBatteryStaple!77",
            FullName: "أحمد شامة",
            FullNameEn: "Ahmed Shama",
            Locale: "ar",
            AcceptedTerms: true,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit-test",
            CorrelationId: Guid.NewGuid().ToString("D"));

        var validator = new RegisterParentCommandValidator(new ZxcvbnPasswordStrengthValidator());
        Assert.Empty(validator.Validate(cmd));

        var outcome = await h.AuthService.RegisterParentAsync(cmd);

        Assert.True(outcome.Success);
        Assert.Equal(201, outcome.HttpStatus);
        Assert.NotNull(outcome.Payload);
        Assert.Equal("family", outcome.Payload!.TenantType);
        Assert.Contains("parent", outcome.Payload.Roles);
        // Registration returns a placeholder envelope — no tokens until verify + login.
        Assert.Equal(string.Empty, outcome.Payload.AccessToken);

        var tenant = await h.Db.IdentityTenants.IgnoreQueryFilters()
            .SingleAsync(t => t.Type == TenantType.Family);
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.TenantId == tenant.Id);
        Assert.Equal(UserStatus.PendingEmailVerification, user.Status);
        Assert.Equal("parent@example.com", user.NormalizedEmail);
        Assert.NotNull(user.PasswordHash);

        // Exactly one email-verification notification was dispatched.
        Assert.Single(h.Notifications.Dispatched);
        var record = h.Notifications.Dispatched[0];
        Assert.Equal("email_verification", record.Kind);
        Assert.StartsWith("http://test.local/verify-email?token=", record.Link);

        // Audit: register_parent succeeded.
        Assert.Contains(h.Audit.Events, e => e.Action == "register_parent" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task RegisterParent_Rejects_Duplicate_Email()
    {
        using var h = await IdentityTestHarness.CreateAsync();

        var cmd = NewRegisterCommand("dup@example.com");
        var first = await h.AuthService.RegisterParentAsync(cmd);
        Assert.True(first.Success);

        var second = await h.AuthService.RegisterParentAsync(cmd with { CorrelationId = Guid.NewGuid().ToString("D") });
        Assert.False(second.Success);
        Assert.Equal(409, second.HttpStatus);
        Assert.Equal("email_taken", second.ErrorCode);
    }

    private static RegisterParentCommand NewRegisterCommand(string email)
        => new(email, "HorseBatteryStaple!77", "Parent", null, "ar", true,
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D"));

    private static string[] JsonNames(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
}
