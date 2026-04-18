using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Muallimi.Api.Leaderboards.LeaderboardComputation;
using Muallimi.Api.Leaderboards.LeaderboardQuery;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Leaderboards;

/// <summary>
/// T142 (US7) — Integration test for privacy-mode projection.
///
/// Each mode produces a distinct display_name surface:
///   • real_name      — full "Layla Ahmed"
///   • first_name_only — "Layla"
///   • pseudonym      — deterministic pool token
/// Admin views always reach the real display name through the
/// <c>real_display_name</c> projection; student/parent views always use
/// the projected <c>display_name</c>.
/// </summary>
public class PrivacyModeTests
{
    [Fact]
    public void Privacy_Projector_Real_Name_Preserves_Full()
    {
        var name = LeaderboardPrivacyProjector.Apply("real_name", "Layla Ahmed", Guid.NewGuid());
        Assert.Equal("Layla Ahmed", name);
    }

    [Fact]
    public void Privacy_Projector_First_Name_Only_Truncates()
    {
        var name = LeaderboardPrivacyProjector.Apply("first_name_only", "Layla Ahmed", Guid.NewGuid());
        Assert.Equal("Layla", name);
    }

    [Fact]
    public void Privacy_Projector_Pseudonym_Is_Deterministic()
    {
        var studentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var a = LeaderboardPrivacyProjector.Apply("pseudonym", "Layla Ahmed", studentId);
        var b = LeaderboardPrivacyProjector.Apply("pseudonym", "Layla Ahmed", studentId);
        Assert.Equal(a, b);
        Assert.DoesNotContain("Layla", a);
    }

    [Fact]
    public async Task Snapshot_Applies_First_Name_Only_By_Default()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LeaderboardHarness(db);
        await harness.SeedAsync(new decimal[] { 0.9m, 0.7m, 0.5m, 0.3m });

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
        Assert.Equal("first_name_only", snapshot!.PrivacyMode);

        var entries = ReadEntries(snapshot);
        Assert.Contains(entries, e => e.display_name == "Layla" && e.real_display_name == "Layla Ahmed");
    }

    [Fact]
    public async Task Snapshot_Applies_Pseudonym_When_Configured()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LeaderboardHarness(db);
        await harness.SeedAsync(new decimal[] { 0.9m, 0.7m, 0.5m, 0.3m });
        await harness.UpsertConfigAsync("pseudonym");

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
        Assert.Equal("pseudonym", snapshot!.PrivacyMode);

        var entries = ReadEntries(snapshot);
        foreach (var e in entries)
        {
            Assert.DoesNotContain(" ", e.display_name);
            Assert.NotEqual(e.real_display_name, e.display_name);
            Assert.DoesNotContain("Layla", e.display_name);
            Assert.DoesNotContain("Ahmed", e.display_name);
        }
    }

    [Fact]
    public async Task Snapshot_Preserves_Real_Names_When_Configured()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LeaderboardHarness(db);
        await harness.SeedAsync(new decimal[] { 0.9m, 0.7m, 0.5m, 0.3m });
        await harness.UpsertConfigAsync("real_name");

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
        var entries = ReadEntries(snapshot!);
        Assert.Contains(entries, e => e.display_name == "Layla Ahmed");
    }

    private static List<LeaderboardComputationService.LeaderboardEntryPayload> ReadEntries(
        Muallimi.Domain.SchoolManagement.LeaderboardSnapshot snapshot)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        return JsonSerializer.Deserialize<List<LeaderboardComputationService.LeaderboardEntryPayload>>(
            snapshot.Entries, options)
            ?? new List<LeaderboardComputationService.LeaderboardEntryPayload>();
    }
}

