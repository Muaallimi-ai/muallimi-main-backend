using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Leaderboards.LeaderboardComputation;
using Muallimi.Api.Leaderboards.LeaderboardQuery;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Leaderboards;

/// <summary>
/// T140 (US7) — Contract test for leaderboard config endpoints.
///
/// Pins the route constants and asserts the privacy-mode enum and
/// GetOrDefault/Upsert round-trip semantics the leaderboard-contract
/// fixes (first_name_only default, admin-only scoping).
/// </summary>
public class LeaderboardConfigTests
{
    [Fact]
    public void Route_Constants_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/leaderboards/config", LeaderboardConfigEndpoints.GetRoute);
        Assert.Equal("/api/school-admin/leaderboards/config", LeaderboardConfigEndpoints.PutRoute);
    }

    [Theory]
    [InlineData("real_name", true)]
    [InlineData("first_name_only", true)]
    [InlineData("pseudonym", true)]
    [InlineData("anonymous", false)]
    [InlineData("", false)]
    public void Privacy_Modes_Are_Pinned(string mode, bool expectValid)
    {
        Assert.Equal(expectValid, PrivacyModes.IsValid(mode));
    }

    [Fact]
    public async Task GetOrDefault_Returns_Default_When_No_Row()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new LeaderboardConfigRepository(db);
        var config = await repo.GetOrDefaultAsync(LeaderboardHarness.TenantAlpha, LeaderboardHarness.SchoolAlpha);
        Assert.Equal("first_name_only", config.PrivacyMode);
        Assert.True(config.LeaderboardEnabled);
        Assert.Equal("[]", config.PerClassOverridesJson);
    }

    [Fact]
    public async Task Upsert_Persists_Then_Updates()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new LeaderboardConfigRepository(db);

        await repo.UpsertAsync(new Muallimi.Domain.SchoolManagement.LeaderboardConfig
        {
            TenantId = LeaderboardHarness.TenantAlpha,
            SchoolTenantId = LeaderboardHarness.SchoolAlpha,
            PrivacyMode = "pseudonym",
            LeaderboardEnabled = true,
            PerClassOverridesJson = "[]",
        });
        await repo.SaveChangesAsync();

        var first = await repo.GetOrDefaultAsync(LeaderboardHarness.TenantAlpha, LeaderboardHarness.SchoolAlpha);
        Assert.Equal("pseudonym", first.PrivacyMode);

        await repo.UpsertAsync(new Muallimi.Domain.SchoolManagement.LeaderboardConfig
        {
            TenantId = LeaderboardHarness.TenantAlpha,
            SchoolTenantId = LeaderboardHarness.SchoolAlpha,
            PrivacyMode = "real_name",
            LeaderboardEnabled = false,
            PerClassOverridesJson = "[]",
        });
        await repo.SaveChangesAsync();

        var second = await repo.GetOrDefaultAsync(LeaderboardHarness.TenantAlpha, LeaderboardHarness.SchoolAlpha);
        Assert.Equal("real_name", second.PrivacyMode);
        Assert.False(second.LeaderboardEnabled);
    }
}
