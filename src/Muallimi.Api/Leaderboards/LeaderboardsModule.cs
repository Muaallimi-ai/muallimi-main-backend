using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Leaderboards.LeaderboardQuery;

namespace Muallimi.Api.Leaderboards;

/// <summary>
/// Phase 5 — Leaderboards module marker. Subfolders (LeaderboardComputation,
/// LeaderboardQuery) own their endpoints, services, repositories, and
/// background services. Wiring happens in US7 (T140–T152).
/// </summary>
public static class LeaderboardsModule
{
    public static IEndpointRouteBuilder MapLeaderboards(this IEndpointRouteBuilder routes)
    {
        routes.MapLeaderboardConfig();
        routes.MapLeaderboardQueries();
        return routes;
    }
}
