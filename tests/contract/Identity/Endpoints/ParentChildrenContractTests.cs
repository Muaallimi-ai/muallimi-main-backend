using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Endpoints;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Identity.Startup;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Audit;
using Muallimi.Application.Identity.Commands;
using Muallimi.Application.Identity.Dtos;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity.Endpoints;

/// <summary>
/// T083 — Contract test for <c>POST/GET/PATCH/DELETE /api/auth/parent/children</c>
/// and <c>POST /api/auth/parent/children/{id}/regenerate-password</c>. Pins:
///   • Route group constants exposed by <see cref="ParentChildrenEndpoints"/>.
///   • Wire shape of <see cref="CreateChildRequest"/> and <see cref="ChildCredentialsOnce"/>
///     (camelCase, plaintext password exclusively on the create / regenerate
///     response, never on <see cref="ChildSummary"/> or <see cref="ChildDetail"/>).
///   • End-to-end call against <see cref="IUserManagementService"/>:
///       — creates Managed user + grants the student role in one unit of work;
///       — emits <c>child_created</c> audit with the parent as actor;
///       — dispatches the identity.child_created notification to the parent.
/// </summary>
public class ParentChildrenContractTests
{
    [Fact]
    public void Route_Constants_Are_Pinned()
    {
        Assert.Equal("/parent/children", ParentChildrenEndpoints.GroupRoute);
        Assert.Equal("/{id:guid}/regenerate-password", ParentChildrenEndpoints.RegenerateSubRoute);
        Assert.Equal("/api/auth", IdentityEndpointRouteBuilderExtensions.IdentityRoutePrefix);
    }

    [Fact]
    public void CreateChildRequest_Body_Shape_Is_CamelCase()
    {
        var names = JsonNames(typeof(CreateChildRequest));
        foreach (var expected in new[]
        {
            "fullName", "fullNameEn", "grade", "gender", "birthday",
            "preferredUsername", "customPassword", "passwordLocale",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void ChildCredentialsOnce_Carries_GeneratedPassword_Field()
    {
        var names = JsonNames(typeof(ChildCredentialsOnce));
        foreach (var expected in new[]
        {
            "userId", "username", "generatedPassword",
            "fullName", "grade", "tenantId", "createdAt",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void ChildSummary_Never_Leaks_Password()
    {
        var names = JsonNames(typeof(ChildSummary));
        Assert.DoesNotContain("generatedPassword", names);
        Assert.DoesNotContain("password", names);
        Assert.DoesNotContain("passwordHash", names);
    }

    [Fact]
    public void ChildDetail_Never_Leaks_Password()
    {
        var names = JsonNames(typeof(ChildDetail));
        Assert.DoesNotContain("generatedPassword", names);
        Assert.DoesNotContain("password", names);
        Assert.DoesNotContain("passwordHash", names);
    }

    [Fact]
    public async Task CreateChild_Returns_Credentials_Once_And_Persists_Managed_Student()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent1@example.com");
        var svc = BuildUserManagementService(h);

        var cmd = new CreateChildCommand(
            ParentUserId: parentId,
            ParentTenantId: parentTenantId,
            FullName: "علي محمد",
            FullNameEn: "Ali Mohamed",
            Grade: 6,
            Gender: "male",
            Birthday: new DateTime(2015, 3, 14),
            PreferredUsername: null,
            CustomPassword: null,
            PasswordLocale: "ar",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit-test",
            CorrelationId: Guid.NewGuid().ToString("D"));
        var result = await svc.CreateChildAsync(cmd);
        Assert.True(result.Success);
        Assert.Equal(201, result.HttpStatus);
        Assert.NotNull(result.Payload);
        Assert.False(string.IsNullOrEmpty(result.Payload!.GeneratedPassword));
        Assert.StartsWith("aly.2015.", result.Payload.Username);

        var child = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.AccountType == AccountType.Managed);
        Assert.Equal(AccountType.Managed, child.AccountType);
        Assert.Equal(parentId, child.ManagedByUserId);
        Assert.Equal(parentTenantId, child.TenantId);
        Assert.Equal(UserStatus.Active, child.Status);
        Assert.Null(child.Email);
        Assert.NotNull(child.Username);
        Assert.NotNull(child.PasswordHash);
        // Hash is NOT the plaintext.
        Assert.NotEqual(result.Payload.GeneratedPassword, child.PasswordHash);

        Assert.Contains(h.Audit.Events, e => e.Action == "child_created" && e.Outcome == "succeeded");
        Assert.Contains(h.Notifications.Dispatched, n => n.Kind == "child_created");
    }

    [Fact]
    public async Task CreateChild_Respects_Custom_Password_And_Preferred_Username()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent2@example.com");
        var svc = BuildUserManagementService(h);

        var cmd = new CreateChildCommand(
            ParentUserId: parentId,
            ParentTenantId: parentTenantId,
            FullName: "سارة أحمد",
            FullNameEn: null,
            Grade: 4,
            Gender: "female",
            Birthday: new DateTime(2017, 8, 2),
            PreferredUsername: "sara.custom",
            CustomPassword: "TopSecret-123!",
            PasswordLocale: "en",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit-test",
            CorrelationId: Guid.NewGuid().ToString("D"));
        var result = await svc.CreateChildAsync(cmd);
        Assert.True(result.Success);
        Assert.Equal("sara.custom", result.Payload!.Username);
        Assert.Equal("TopSecret-123!", result.Payload.GeneratedPassword);
    }

    [Fact]
    public async Task CreateChild_Rejects_Used_PreferredUsername()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent3@example.com");
        var svc = BuildUserManagementService(h);

        var baseCmd = new CreateChildCommand(
            parentId, parentTenantId,
            "خالد", null, 5, "male",
            new DateTime(2016, 1, 1),
            "khaled.custom", null, "ar",
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D"));
        var first = await svc.CreateChildAsync(baseCmd);
        Assert.True(first.Success);

        var second = await svc.CreateChildAsync(baseCmd with { CorrelationId = Guid.NewGuid().ToString("D"), FullName = "ابن خالد" });
        Assert.False(second.Success);
        Assert.Equal(409, second.HttpStatus);
        Assert.Equal("username_unavailable", second.ErrorCode);
    }

    [Fact]
    public async Task List_Returns_Summaries_Without_Password_Fields()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent4@example.com");
        var svc = BuildUserManagementService(h);

        await svc.CreateChildAsync(NewCreateCmd(parentId, parentTenantId, "ليلى"));
        await svc.CreateChildAsync(NewCreateCmd(parentId, parentTenantId, "يوسف"));

        var list = await svc.ListChildrenAsync(parentId, parentTenantId);
        Assert.Equal(2, list.Count);
        foreach (var c in list)
        {
            // ChildSummary has no password field by construction; ensure
            // none of the properties are named like one.
            var password = typeof(ChildSummary).GetProperties().Any(p => p.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.False(password);
            Assert.False(string.IsNullOrEmpty(c.Username));
        }
    }

    [Fact]
    public async Task RegeneratePassword_Issues_New_Password_And_Revokes_Sessions()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent5@example.com");
        var svc = BuildUserManagementService(h);

        var created = await svc.CreateChildAsync(NewCreateCmd(parentId, parentTenantId, "نور"));
        Assert.True(created.Success);
        var childId = Guid.Parse(created.Payload!.UserId);
        var firstPassword = created.Payload.GeneratedPassword;

        var regen = await svc.RegenerateChildPasswordAsync(new RegenerateChildPasswordCommand(
            parentId, parentTenantId, childId,
            CustomPassword: null, PasswordLocale: "ar",
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(regen.Success);
        Assert.NotEqual(firstPassword, regen.Payload!.GeneratedPassword);
        Assert.Contains(h.Audit.Events, e => e.Action == "child_password_regenerated" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task UpdateChild_Updates_Full_Name_And_Audits()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent6@example.com");
        var svc = BuildUserManagementService(h);

        var created = await svc.CreateChildAsync(NewCreateCmd(parentId, parentTenantId, "حسن"));
        var childId = Guid.Parse(created.Payload!.UserId);

        var update = await svc.UpdateChildAsync(new UpdateChildCommand(
            parentId, parentTenantId, childId,
            FullName: "حسن المحدّث",
            FullNameEn: "Hassan Updated",
            Grade: 7, Gender: null, Birthday: null,
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(update.Success);
        Assert.Equal("حسن المحدّث", update.Payload!.FullName);
        Assert.Contains(h.Audit.Events, e => e.Action == "child_updated" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task DeleteChild_Archives_And_Removes_From_List()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent7@example.com");
        var svc = BuildUserManagementService(h);

        var created = await svc.CreateChildAsync(NewCreateCmd(parentId, parentTenantId, "ريم"));
        var childId = Guid.Parse(created.Payload!.UserId);

        var del = await svc.DeleteChildAsync(new DeleteChildCommand(
            parentId, parentTenantId, childId,
            "127.0.0.1", null, Guid.NewGuid().ToString("D")));
        Assert.True(del.Success);

        var list = await svc.ListChildrenAsync(parentId, parentTenantId);
        Assert.Empty(list);
        Assert.Contains(h.Audit.Events, e => e.Action == "child_deleted" && e.Outcome == "succeeded");
    }

    [Fact]
    public async Task CreateChild_Persists_Grade_Birthday_Gender_To_StudentProfile()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent-profile@example.com");
        var svc = BuildUserManagementService(h);

        var birthday = new DateTime(2015, 3, 14);
        var cmd = new CreateChildCommand(
            ParentUserId: parentId,
            ParentTenantId: parentTenantId,
            FullName: "منى",
            FullNameEn: null,
            Grade: 6,
            Gender: "female",
            Birthday: birthday,
            PreferredUsername: null,
            CustomPassword: null,
            PasswordLocale: "ar",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D"));
        var created = await svc.CreateChildAsync(cmd);
        Assert.True(created.Success);
        var childId = Guid.Parse(created.Payload!.UserId);

        var profile = await h.Db.StudentProfiles.IgnoreQueryFilters()
            .SingleAsync(p => p.UserId == childId);
        Assert.Equal("6", profile.Grade);
        Assert.Equal("female", profile.Gender);
        Assert.Equal(DateOnly.FromDateTime(birthday), profile.Birthday);
        Assert.Equal(parentTenantId, profile.TenantId);

        // And the GET read-back projects the real values now, not defaults.
        var detail = await svc.GetChildAsync(parentId, childId);
        Assert.NotNull(detail);
        Assert.Equal(6, detail!.Grade);
        Assert.Equal("female", detail.Gender);
        Assert.Equal(birthday.Date, detail.Birthday.Date);

        var list = await svc.ListChildrenAsync(parentId, parentTenantId);
        var listed = Assert.Single(list);
        Assert.Equal(6, listed.Grade);
        Assert.Equal("female", listed.Gender);
    }

    [Fact]
    public async Task CreateChild_Accepts_KG2_Grade_Zero()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent-kg2@example.com");
        var svc = BuildUserManagementService(h);

        var cmd = new CreateChildCommand(
            parentId, parentTenantId, "روضة", null,
            Grade: 0, Gender: "male",
            Birthday: new DateTime(2020, 5, 1),
            null, null, "ar",
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D"));
        var created = await svc.CreateChildAsync(cmd);
        Assert.True(created.Success);
        Assert.Equal(0, created.Payload!.Grade);

        var childId = Guid.Parse(created.Payload.UserId);
        var profile = await h.Db.StudentProfiles.IgnoreQueryFilters()
            .SingleAsync(p => p.UserId == childId);
        Assert.Equal("0", profile.Grade);
    }

    [Fact]
    public async Task UpdateChild_Persists_Grade_Birthday_Gender_To_StudentProfile()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent-upd@example.com");
        var svc = BuildUserManagementService(h);

        var created = await svc.CreateChildAsync(NewCreateCmd(parentId, parentTenantId, "سلمى"));
        var childId = Guid.Parse(created.Payload!.UserId);

        var newBirthday = new DateTime(2014, 7, 20);
        var update = await svc.UpdateChildAsync(new UpdateChildCommand(
            parentId, parentTenantId, childId,
            FullName: null, FullNameEn: null,
            Grade: 8, Gender: "female", Birthday: newBirthday,
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(update.Success);
        Assert.Equal(8, update.Payload!.Grade);
        Assert.Equal("female", update.Payload.Gender);
        Assert.Equal(newBirthday.Date, update.Payload.Birthday.Date);

        var profile = await h.Db.StudentProfiles.IgnoreQueryFilters()
            .SingleAsync(p => p.UserId == childId);
        Assert.Equal("8", profile.Grade);
        Assert.Equal("female", profile.Gender);
        Assert.Equal(DateOnly.FromDateTime(newBirthday), profile.Birthday);
    }

    [Fact]
    public async Task UpdateChild_Null_Birthday_Does_Not_Clear_Persisted_Value()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent-nullbd@example.com");
        var svc = BuildUserManagementService(h);

        var birthday = new DateTime(2016, 2, 2);
        var created = await svc.CreateChildAsync(new CreateChildCommand(
            parentId, parentTenantId, "نادر", null,
            Grade: 4, Gender: "male", Birthday: birthday,
            null, null, "ar",
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D")));
        var childId = Guid.Parse(created.Payload!.UserId);

        // Patch the grade, leave birthday null (typical "edit just grade" flow).
        var update = await svc.UpdateChildAsync(new UpdateChildCommand(
            parentId, parentTenantId, childId,
            FullName: null, FullNameEn: null,
            Grade: 5, Gender: null, Birthday: null,
            IpAddress: "127.0.0.1", UserAgent: null,
            CorrelationId: Guid.NewGuid().ToString("D")));
        Assert.True(update.Success);

        var profile = await h.Db.StudentProfiles.IgnoreQueryFilters()
            .SingleAsync(p => p.UserId == childId);
        Assert.Equal("5", profile.Grade);
        Assert.Equal(DateOnly.FromDateTime(birthday), profile.Birthday); // untouched
    }

    [Fact]
    public async Task GetChild_Without_StudentProfile_Emits_Defaults()
    {
        // Legacy child created before the StudentProfile persistence fix.
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await RegisterAndVerifyParentAsync(h, "parent-legacy@example.com");
        var svc = BuildUserManagementService(h);

        // Seed a Managed child directly (bypass the service) to simulate
        // a pre-fix legacy row with no StudentProfile.
        var studentRole = await h.Db.IdentityRoles.IgnoreQueryFilters()
            .SingleAsync(r => r.Name == "student");
        var childId = Guid.NewGuid();
        h.Db.IdentityUsers.Add(new Muallimi.Domain.Identity.Entities.User
        {
            Id = childId,
            TenantId = parentTenantId,
            AccountType = AccountType.Managed,
            ManagedByUserId = parentId,
            Username = "legacy.kid.001",
            NormalizedUsername = "legacy.kid.001",
            FullName = "طفل قديم",
            Locale = "ar",
            Status = UserStatus.Active,
            PasswordHash = "$2a$12$fake",
            PasswordChangedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = parentId,
        });
        h.Db.IdentityUserRoles.Add(new Muallimi.Domain.Identity.Entities.UserRole
        {
            Id = Guid.NewGuid(),
            UserId = childId,
            RoleId = studentRole.Id,
            TenantId = parentTenantId,
            GrantedBy = parentId,
            GrantedAt = DateTime.UtcNow,
        });
        await h.Db.SaveChangesAsync();

        var detail = await svc.GetChildAsync(parentId, childId);
        Assert.NotNull(detail);
        Assert.Equal(0, detail!.Grade);
        Assert.Equal(string.Empty, detail.Gender);
        Assert.Equal(DateTime.MinValue, detail.Birthday);
    }

    [Fact]
    public async Task CrossParent_Access_Returns_NotFound()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (p1, t1) = await RegisterAndVerifyParentAsync(h, "pA@example.com");
        var (p2, _) = await RegisterAndVerifyParentAsync(h, "pB@example.com");
        var svc = BuildUserManagementService(h);

        var created = await svc.CreateChildAsync(NewCreateCmd(p1, t1, "مي"));
        var childId = Guid.Parse(created.Payload!.UserId);

        var detail = await svc.GetChildAsync(p2, childId);
        Assert.Null(detail);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static CreateChildCommand NewCreateCmd(Guid parentId, Guid tenantId, string name)
        => new(parentId, tenantId, name, null, 5, "male",
            new DateTime(2015, 1, 1), null, null, "ar",
            "127.0.0.1", "xunit", Guid.NewGuid().ToString("D"));

    private static async Task<(Guid UserId, Guid TenantId)> RegisterAndVerifyParentAsync(IdentityTestHarness h, string email)
    {
        var cmd = new RegisterParentCommand(
            Email: email,
            Password: "HorseBatteryStaple!77",
            FullName: "الوالد " + email,
            FullNameEn: null,
            Locale: "ar",
            AcceptedTerms: true,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D"));
        var outcome = await h.AuthService.RegisterParentAsync(cmd);
        Assert.True(outcome.Success);
        var normalized = email.ToLowerInvariant();
        var user = await h.Db.IdentityUsers.IgnoreQueryFilters()
            .SingleAsync(u => u.NormalizedEmail == normalized);
        // Activate the parent so downstream services see a usable actor.
        user.VerifyEmail();
        await h.Db.SaveChangesAsync();
        return (user.Id, user.TenantId);
    }

    private static UserManagementService BuildUserManagementService(IdentityTestHarness h)
        => new(
            h.Db,
            h.Passwords,
            new UsernameGenerator(new Random(1234)),
            new ChildPasswordGenerator(new Random(4321)),
            h.Audit.Emitter,
            h.Notifications,
            NullLogger<UserManagementService>.Instance);

    private static string[] JsonNames(Type t)
        => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .ToArray();
}
