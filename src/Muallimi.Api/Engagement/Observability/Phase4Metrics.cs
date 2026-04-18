using System.Diagnostics.Metrics;

namespace Muallimi.Api.Engagement.Observability;

/// <summary>
/// T043 (US4) — Phase 4 shared metric instrumentation.
///
/// Counters and histograms exposed on the <c>muallimi.phase4</c> meter so the
/// local Seq dashboard and the production Azure Monitor pipeline can observe
/// ingestion latency, ingestion rate, dead-letter rate, mastery recompute
/// count, streak changes, badge awards, downstream dispatch, and the parent
/// dashboard cache hit rate (wired in later stories). The individual
/// services call into these instruments — do not create parallel meters.
/// </summary>
public static class Phase4Metrics
{
    public const string MeterName = "muallimi.phase4";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> IngestionInserted =
        Meter.CreateCounter<long>("phase4.ingestion.inserted", unit: "events",
            description: "Progress records inserted from Phase 3 session events.");

    public static readonly Counter<long> IngestionDuplicate =
        Meter.CreateCounter<long>("phase4.ingestion.duplicate", unit: "events",
            description: "Duplicate Phase 3 session events rejected by the (tenant_id, source_event_id) UNIQUE constraint.");

    public static readonly Counter<long> IngestionDeadLettered =
        Meter.CreateCounter<long>("phase4.ingestion.dead_lettered", unit: "events",
            description: "Permanently rejected Phase 3 session events written to the dead-letter store.");

    public static readonly Histogram<double> IngestionLatencyMs =
        Meter.CreateHistogram<double>("phase4.ingestion.latency_ms", unit: "ms",
            description: "End-to-end ingestion latency per event: record insert + mastery recompute + streak + badge + outbox.");

    public static readonly Counter<long> MasteryRecomputed =
        Meter.CreateCounter<long>("phase4.mastery.recomputed", unit: "updates",
            description: "Mastery state rows recomputed (including no-op updates).");

    public static readonly Counter<long> MasteryChanged =
        Meter.CreateCounter<long>("phase4.mastery.changed", unit: "updates",
            description: "Mastery state rows with a score or band change.");

    public static readonly Counter<long> StreakChanged =
        Meter.CreateCounter<long>("phase4.streak.changed", unit: "updates",
            description: "Streak state transitions (increment, reset, length change).");

    public static readonly Counter<long> BadgeAwarded =
        Meter.CreateCounter<long>("phase4.badge.awarded", unit: "badges",
            description: "New badge awards produced by the evaluator.");

    public static readonly Counter<long> DownstreamEnqueued =
        Meter.CreateCounter<long>("phase4.downstream.enqueued", unit: "events",
            description: "Rows added to the Phase 4 downstream outbox.");

    public static readonly Counter<long> DashboardCacheHits =
        Meter.CreateCounter<long>("phase4.dashboard.cache_hits", unit: "queries",
            description: "Parent dashboard queries served from the short-TTL cache.");

    public static readonly Counter<long> DashboardCacheMisses =
        Meter.CreateCounter<long>("phase4.dashboard.cache_misses", unit: "queries",
            description: "Parent dashboard queries that recomputed instead of hitting the cache.");
}
