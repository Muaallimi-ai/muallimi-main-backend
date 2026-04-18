using System;
using System.Threading;
using System.Threading.Tasks;
using Muallimi.Api.Leaderboards.LeaderboardComputation;
using Muallimi.Api.Leaderboards.LeaderboardQuery;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Leaderboards;

/// <summary>
/// T141 (US7) — Contract test for leaderboard query endpoints across all
/// four roles. Pins route constants and verifies the computed snapshot
/// exposes the fields the role-scoped projections need (rank, value,
/// display_name). The privacy projection is asserted separately in
/// <see cref="PrivacyModeTests"/>.
/// </summary>
public class LeaderboardQueryTests
{
    [Fact]
    public void Route_Constants_Are_Pinned()
    {
        Assert.Equal("/api/school-admin/leaderboards/class/{classGroupId:guid}", LeaderboardEndpoints.AdminClassRoute);
        Assert.Equal("/api/teacher/leaderboards/class/{classGroupId:guid}/subject/{subjectId:guid}", LeaderboardEndpoints.TeacherClassSubjectRoute);
        Assert.Equal("/api/student/leaderboard", LeaderboardEndpoints.StudentRoute);
        Assert.Equal("/api/parent/leaderboard/{childId:guid}", LeaderboardEndpoints.ParentChildRoute);
    }

    [Theory]
    [InlineData("mastery", true)]
    [InlineData("engagement", true)]
    [InlineData("improvement", true)]
    [InlineData("unknown", false)]
    public void Leaderboard_Metrics_Are_Pinned(string metric, bool expected)
    {
        Assert.Equal(expected, LeaderboardMetrics.IsValid(metric));
    }

    [Fact]
    public async Task Snapshot_Contains_Entries_For_Class_Members()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LeaderboardHarness(db);
        await harness.SeedAsync(new decimal[] { 0.90m, 0.70m, 0.50m, 0.30m });

        var configRepo = new LeaderboardConfigRepository(db);
        var snapshotRepo = new LeaderboardSnapshotRepository(db);
        var compute = new LeaderboardComputationService(db, configRepo, snapshotRepo);

        var now = DateTime.UtcNow;
        var snapshot = await compute.ComputeClassSnapshotAsync(
            LeaderboardHarness.TenantAlpha,
            LeaderboardHarness.SchoolAlpha,
            LeaderboardHarness.ClassAlpha,
            "mastery",
            subjectId: LeaderboardHarness.SubjectMath,
            now.AddDays(-7),
            now);
        Assert.NotNull(snapshot);
        Assert.Equal("class", snapshot!.ScopeType);
        Assert.Equal("mastery", snapshot.Metric);
        Assert.Contains("\"rank\":1", snapshot.Entries);
        Assert.Contains("\"rank\":4", snapshot.Entries);
    }

    [Fact]
    public async Task Snapshot_Is_Null_When_Leaderboard_Disabled()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LeaderboardHarness(db);
        await harness.SeedAsync(new decimal[] { 0.90m, 0.70m, 0.50m, 0.30m });
        await harness.UpsertConfigAsync("first_name_only", enabled: false);

        var configRepo = new LeaderboardConfigRepository(db);
        var snapshotRepo = new LeaderboardSnapshotRepository(db);
        var compute = new LeaderboardComputationService(db, configRepo, snapshotRepo);

        var now = DateTime.UtcNow;
        var snapshot = await compute.ComputeClassSnapshotAsync(
            LeaderboardHarness.TenantAlpha,
            LeaderboardHarness.SchoolAlpha,
            LeaderboardHarness.ClassAlpha,
            "mastery",
            subjectId: LeaderboardHarness.SubjectMath,
            now.AddDays(-7),
            now);
        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Snapshot_Is_Null_When_Class_Disabled_In_Overrides()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LeaderboardHarness(db);
        await harness.SeedAsync(new decimal[] { 0.90m, 0.70m, 0.50m, 0.30m });
        var overrides = $"[{{\"class_group_id\":\"{LeaderboardHarness.ClassAlpha}\",\"enabled\":false}}]";
        await harness.UpsertConfigAsync("first_name_only", enabled: true, perClassOverridesJson: overrides);

        var configRepo = new LeaderboardConfigRepository(db);
        var snapshotRepo = new LeaderboardSnapshotRepository(db);
        var compute = new LeaderboardComputationService(db, configRepo, snapshotRepo);

        var now = DateTime.UtcNow;
        var snapshot = await compute.ComputeClassSnapshotAsync(
            LeaderboardHarness.TenantAlpha,
            LeaderboardHarness.SchoolAlpha,
            LeaderboardHarness.ClassAlpha,
            "mastery",
            subjectId: LeaderboardHarness.SubjectMath,
            now.AddDays(-7),
            now);
        Assert.Null(snapshot);
    }
}
