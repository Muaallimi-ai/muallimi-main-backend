using System;
using System.Threading.Tasks;
using Muallimi.Api.Announcements.AnnouncementDispatch;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Announcements;

/// <summary>
/// T155 (US8) — integration test for <see cref="AnnouncementTargetResolver"/>.
///
/// Exercises the three scopes (class, grade, school) and asserts the
/// expected recipient counts from the seeded harness. The resolver is
/// the single place where class / grade / school scope expansion is
/// computed, so this test doubles as the contract gate for the invariant
/// "targeting resolves at publish time".
/// </summary>
public class TargetingResolutionTests
{
    [Fact]
    public async Task Class_Scope_Resolves_To_Active_Enrolments_And_Linked_Parents()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var resolver = new AnnouncementTargetResolver(db);
        var resolution = await resolver.ResolveAsync(
            AnnouncementHarness.TenantAlpha,
            AnnouncementHarness.SchoolAlpha,
            "class",
            AnnouncementHarness.ClassAlpha7A.ToString("D"),
            DateTime.UtcNow);

        // 4 active students (transferred one is excluded) + 1 parent.
        Assert.Equal(5, resolution.Recipients.Count);
        Assert.Equal(4, resolution.StudentCount);
        Assert.Equal(1, resolution.ParentCount);
    }

    [Fact]
    public async Task Grade_Scope_Expands_To_All_Classes_In_Grade()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var resolver = new AnnouncementTargetResolver(db);
        var resolution = await resolver.ResolveAsync(
            AnnouncementHarness.TenantAlpha,
            AnnouncementHarness.SchoolAlpha,
            "grade",
            "7",
            DateTime.UtcNow);

        // 4 Alpha7A + 3 Alpha7B = 7 students + 1 parent = 8 recipients.
        Assert.Equal(7, resolution.StudentCount);
        Assert.Equal(1, resolution.ParentCount);
    }

    [Fact]
    public async Task School_Scope_Includes_All_Active_Classes_But_Not_Other_Tenants()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var resolver = new AnnouncementTargetResolver(db);
        var resolution = await resolver.ResolveAsync(
            AnnouncementHarness.TenantAlpha,
            AnnouncementHarness.SchoolAlpha,
            "school",
            targetRaw: null,
            DateTime.UtcNow);

        Assert.Equal(7, resolution.StudentCount);
        Assert.Equal(1, resolution.ParentCount);
        foreach (var betaStudent in harness.BetaStudents)
        {
            Assert.DoesNotContain(resolution.Recipients, r => r.RecipientId == betaStudent);
        }
    }

    [Fact]
    public async Task Invalid_Target_Id_Produces_Empty_Recipient_List()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new AnnouncementHarness(db);
        await harness.SeedAsync();

        var resolver = new AnnouncementTargetResolver(db);
        var bogus = await resolver.ResolveAsync(
            AnnouncementHarness.TenantAlpha,
            AnnouncementHarness.SchoolAlpha,
            "class",
            "not-a-guid",
            DateTime.UtcNow);
        Assert.Empty(bogus.Recipients);

        var wrongGrade = await resolver.ResolveAsync(
            AnnouncementHarness.TenantAlpha,
            AnnouncementHarness.SchoolAlpha,
            "grade",
            "99",
            DateTime.UtcNow);
        Assert.Empty(wrongGrade.Recipients);
    }
}
