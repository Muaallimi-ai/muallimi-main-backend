using System.Collections.Generic;
using System.Linq;

namespace Muallimi.Api.Leaderboards.LeaderboardComputation;

/// <summary>
/// T145 (US7) — Ranking calculator with standard-competition tie
/// handling: equal values share a rank, the next rank after a tie skips
/// (1, 2, 2, 4). Input ordering is descending by value.
/// </summary>
public sealed record RankedEntry(int Rank, RankInput Input);

public sealed record RankInput(object Key, decimal Value);

public static class LeaderboardRankingCalculator
{
    public static IReadOnlyList<RankedEntry> Rank(IEnumerable<RankInput> inputs)
    {
        var ordered = inputs.OrderByDescending(x => x.Value).ToList();
        var result = new List<RankedEntry>(ordered.Count);
        var rank = 0;
        var sharedCount = 0;
        decimal? lastValue = null;

        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            if (lastValue is null || entry.Value != lastValue.Value)
            {
                rank = i + 1;
                sharedCount = 1;
                lastValue = entry.Value;
            }
            else
            {
                sharedCount++;
            }
            result.Add(new RankedEntry(rank, entry));
        }

        return result;
    }
}
