using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Identity.Services;
using Muallimi.Api.Tests.Identity;
using Muallimi.Application.Identity.Services;
using Muallimi.Application.Identity.Validators;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Identity;

/// <summary>
/// Phase 9 follow-up — duplicate-child detection.
///
/// A parent who attempts to create a second child with the same
/// normalized full-name + (birth year, birth month) as an existing
/// child must receive a 409 `duplicate_child` envelope carrying the
/// existing child's id. Re-submitting the same payload with
/// `ConfirmDuplicate=true` (the "twins — add anyway" path) succeeds
/// and writes a `child_duplicate_override` audit row.
///
/// Different birthdays, different normalized names, and a separate
/// parent's children must NOT trigger the conflict — duplicate scope
/// is per-parent, not tenant-wide.
/// </summary>
public class ParentDuplicateChildDetectionTests
{
    private static UserManagementService NewService(IdentityTestHarness h) =>
        new UserManagementService(
            h.Db, h.Passwords,
            new UsernameGenerator(new Random(7)),
            new ChildPasswordGenerator(new Random(7)),
            h.Audit.Emitter, h.Notifications,
            NullLogger<UserManagementService>.Instance,
            new WeakPinBlocklist(),
            new AlwaysFreshManagerReAuth(),
            new InMemoryCredentialAuditWriter(),
            new ZxcvbnPasswordStrengthValidator());

    [Fact]
    public async Task Duplicate_Child_Same_Name_And_Birth_Returns_409_With_Existing_Id()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("dup-parent-1@example.com");
        var svc = NewService(h);

        var first = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId,
            parentTenantId: parentTenantId,
            fullName: "علي شامة",
            grade: 3,
            birthYear: 2018,
            birthMonth: 5));
        Assert.True(first.Success);

        // Same parent, identical name + birthday → must conflict.
        var second = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId,
            parentTenantId: parentTenantId,
            fullName: "علي شامة",
            grade: 3,
            birthYear: 2018,
            birthMonth: 5));
        Assert.False(second.Success);
        Assert.Equal(409, second.HttpStatus);
        Assert.Equal("duplicate_child", second.ErrorCode);
        Assert.NotNull(second.Errors);
        var err = second.Errors!.Single();
        Assert.Equal("duplicate_child", err.Code);
        // The envelope packs the existing child's id into Field so the
        // frontend can offer "open existing child".
        Assert.Equal(first.Payload!.UserId, err.Field);
    }

    [Fact]
    public async Task Duplicate_Folds_Arabic_Variants_And_Case()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("dup-parent-ar@example.com");
        var svc = NewService(h);

        // First child: Arabic with ta-marbuta + diacritics
        await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "سَارة أحمد", birthYear: 2017, birthMonth: 9));

        // Second attempt: same name with ta-marbuta dropped to ha + diacritics removed + extra spaces
        var conflict = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "سارة  احمد", birthYear: 2017, birthMonth: 9));
        Assert.False(conflict.Success);
        Assert.Equal("duplicate_child", conflict.ErrorCode);
    }

    [Fact]
    public async Task Duplicate_Folds_English_Case_And_Whitespace()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("dup-parent-en@example.com");
        var svc = NewService(h);

        await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "Ali Shama", birthYear: 2018, birthMonth: 5));

        var conflict = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "ALI  SHAMA", birthYear: 2018, birthMonth: 5));
        Assert.False(conflict.Success);
        Assert.Equal("duplicate_child", conflict.ErrorCode);
    }

    [Fact]
    public async Task Different_Birth_Month_Allows_Creation()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("dup-parent-2@example.com");
        var svc = NewService(h);

        await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "علي شامة", birthYear: 2018, birthMonth: 5));

        // Same name but a different birth month → not a duplicate.
        var ok = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "علي شامة", birthYear: 2018, birthMonth: 8));
        Assert.True(ok.Success);
    }

    [Fact]
    public async Task ConfirmDuplicate_Allows_Override_And_Audits()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentId, parentTenantId) = await h.SeedVerifiedParentAsync("dup-parent-3@example.com");
        var svc = NewService(h);

        var first = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "علي شامة", birthYear: 2018, birthMonth: 5));
        Assert.True(first.Success);

        // Twin scenario — explicit confirm.
        var twin = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentId, parentTenantId: parentTenantId,
            fullName: "علي شامة", birthYear: 2018, birthMonth: 5)
            with { ConfirmDuplicate = true });
        Assert.True(twin.Success);
        Assert.NotEqual(first.Payload!.UserId, twin.Payload!.UserId);

        // Audit row was emitted with the override action.
        Assert.Contains(h.Audit.Events,
            ev => ev.Action == "child_duplicate_override"
               && ev.TargetId == first.Payload.UserId);
    }

    [Fact]
    public async Task Different_Parents_With_Same_Child_Identity_Both_Allowed()
    {
        using var h = await IdentityTestHarness.CreateAsync();
        var (parentA, parentATenant) = await h.SeedVerifiedParentAsync("dup-parent-a@example.com");
        var (parentB, parentBTenant) = await h.SeedVerifiedParentAsync("dup-parent-b@example.com");
        var svc = NewService(h);

        await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentA, parentTenantId: parentATenant,
            fullName: "علي شامة", birthYear: 2018, birthMonth: 5));

        // Different parent — even though every field matches, this is
        // a different real-world child. Must be allowed.
        var ok = await svc.CreateChildAsync(ChildCommandFixtures.MakeCreateChild(
            parentUserId: parentB, parentTenantId: parentBTenant,
            fullName: "علي شامة", birthYear: 2018, birthMonth: 5));
        Assert.True(ok.Success);
    }
}
