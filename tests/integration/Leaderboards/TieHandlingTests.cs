using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Muallimi.Api.Leaderboards.LeaderboardComputation;
using Muallimi.Api.Leaderboards.LeaderboardQuery;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Leaderboards;

/// <summary>
/// T143 (US7) — Integration test for standard-competition tie handling.
///
/// Inputs with equal values share a rank; the rank immediately following
/// a tie skips (1, 2, 2, 4). The ranking calculator is deterministic so
/// the leaderboard contract ("ties are handled by assigning the same rank
/// to tied entries; the next rank after a tie skips") holds regardless
/// of insertion order.
/// </summary>
public class TieHandlingTests
{
    [Fact]
    public void Calculator_Assigns_Shared_Rank_For_Ties()
    {
        var inputs = new[]
        {
            new RankInput("A", 10m),
            new RankInput("B", 8m),
            new RankInput("C", 8m),
            new RankInput("D", 5m),
        };
        var ranked = LeaderboardRankingCalculator.Rank(inputs);
        var byKey = ranked.ToDictionary(r => (string)r.Input.Key, r => r.Rank);
        Assert.Equal(1, byKey["A"]);
        Assert.Equal(2, byKey["B"]);
        Assert.Equal(2, byKey["C"]);
        Assert.Equal(4, byKey["D"]);
    }

    [Fact]
    public void Calculator_Handles_Triple_Tie()
    {
        var inputs = new[]
        {
            new RankInput("A", 10m),
            new RankInput("B", 7m),
            new RankInput("C", 7m),
            new RankInput("D", 7m),
            new RankInput("E", 2m),
        };
        var ranked = LeaderboardRankingCalculator.Rank(inputs);
        var byKey = ranked.ToDictionary(r => (string)r.Input.Key, r => r.Rank);
        Assert.Equal(1, byKey["A"]);
        Assert.Equal(2, byKey["B"]);
        Assert.Equal(2, byKey["C"]);
        Assert.Equal(2, byKey["D"]);
        Assert.Equal(5, byKey["E"]);
    }

    [Fact]
    public async Task Snapshot_Preserves_Ties_From_Identical_Mastery_Scores()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LeaderboardHarness(db);
        await harness.SeedAsync(new decimal[] { 0.9m, 0.7m, 0.7m, 0.5m });

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

        var entries = JsonSerializer.Deserialize<List<LeaderboardComputationService.LeaderboardEntryPayload>>(
            snapshot!.Entries,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? new();
        var ranksByStudent = entries.ToDictionary(e => e.student_id, e => e.rank);
        Assert.Equal(1, ranksByStudent[harness.Student1]);
        Assert.Equal(2, ranksByStudent[harness.Student2]);
        Assert.Equal(2, ranksByStudent[harness.Student3]);
        Assert.Equal(4, ranksByStudent[harness.Student4]);
    }
}
