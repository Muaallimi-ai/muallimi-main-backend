using Muallimi.Domain.Shared;

namespace Muallimi.Api.Coverage;

/// <summary>
/// T117 - Surfaces queue age against the BRD SLA thresholds:
///   TextSummary / Audio / QuizItem / QaCacheEntry = 5 business days
///   Visual / visual format variants                = 7 business days
///
/// "Business day" = Monday–Friday in UTC. Weekends do not advance the clock.
/// This is intentionally coarse — the BRD SLA is a managerial alarm, not a
/// contractual SLA — but it is deterministic so both backend and dashboard
/// report identical numbers.
/// </summary>
public static class QueueAgeCalculator
{
    public const int TextAudioSlaBusinessDays = 5;
    public const int VisualSlaBusinessDays = 7;

    public static int SlaThresholdBusinessDays(AssetType assetType) => assetType switch
    {
        AssetType.Visual => VisualSlaBusinessDays,
        _ => TextAudioSlaBusinessDays
    };

    /// <summary>
    /// Counts full business days (Mon–Fri UTC) between <paramref name="start"/>
    /// and <paramref name="now"/>. Same-day returns 0. If <paramref name="start"/>
    /// is after <paramref name="now"/> returns 0.
    /// </summary>
    public static int BusinessDaysBetween(DateTime start, DateTime now)
    {
        var from = DateTime.SpecifyKind(start.Date, DateTimeKind.Utc);
        var to = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        if (to <= from) return 0;

        int days = 0;
        for (var d = from; d < to; d = d.AddDays(1))
        {
            if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
                days++;
        }
        return days;
    }

    /// <summary>
    /// True when the asset has been in its current queue longer than the
    /// BRD threshold. A null anchor (state that has never entered a queue)
    /// never breaches SLA.
    /// </summary>
    public static bool IsSlaBreached(AssetType assetType, DateTime? anchor, DateTime now)
    {
        if (anchor is null) return false;
        var age = BusinessDaysBetween(anchor.Value, now);
        return age > SlaThresholdBusinessDays(assetType);
    }

    /// <summary>
    /// Returns the computed business-day age for the supplied anchor, or
    /// zero when there is no anchor (e.g. NotStarted states).
    /// </summary>
    public static int BusinessDayAge(DateTime? anchor, DateTime now)
        => anchor is null ? 0 : BusinessDaysBetween(anchor.Value, now);
}
