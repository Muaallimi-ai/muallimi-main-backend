using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Muallimi.Api.StudentExperience.HomeDashboard;
using Muallimi.Api.StudentExperience.StudentSession;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.StudentExperience.HomeDashboard;

/// <summary>
/// T028 (US1) — Contract for GET /student/plan-gate/snapshot.
///
/// The snapshot is advisory only. The contract requires the response to
/// include plan_tier_snapshot, all seven mode_tile_states, and an
/// expires_at hint so the UI knows when to re-fetch. Every gated path
/// on the backend re-checks plan gate at request time regardless.
/// </summary>
public class PlanGateSnapshotContractTests
{
    [Fact]
    public void PlanGateSnapshotResponse_Shape_Matches_Contract()
    {
        var props = typeof(PlanGateSnapshotResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Contains("SessionId", props);
        Assert.Contains("PlanTierSnapshot", props);
        Assert.Contains("ModeTileStates", props);
        Assert.Contains("ExpiresAt", props);
    }

    [Fact]
    public async Task ResolveTiles_Enumerates_Free_Standard_Premium_Deterministically()
    {
        var gate = new AllowAllPlanGate();
        var service = new HomeDashboardService(gate, db: null!);

        var free = (await service.ResolveTilesAsync(Guid.NewGuid(), "free")).Select(t => t.Mode).ToArray();
        var std  = (await service.ResolveTilesAsync(Guid.NewGuid(), "standard")).Select(t => t.Mode).ToArray();
        var prem = (await service.ResolveTilesAsync(Guid.NewGuid(), "premium")).Select(t => t.Mode).ToArray();

        Assert.Equal(free, std);
        Assert.Equal(std, prem);
        Assert.Equal(7, free.Length);
    }

    [Fact]
    public async Task Whiteboard_Tile_Remains_Subject_Gate_Na_At_Dashboard_Level()
    {
        // Subject gate is re-checked at whiteboard entry (not at dashboard)
        // so the top-level tile reports `na`. This matches the contract's
        // note that plan_gate / subject_gate signal "at this moment".
        var service = new HomeDashboardService(new AllowAllPlanGate(), db: null!);
        var tiles = await service.ResolveTilesAsync(Guid.NewGuid(), "premium");

        var whiteboard = tiles.Single(t => t.Mode == StudentModes.Whiteboard);
        Assert.Equal("na", whiteboard.SubjectGate);
    }
}
