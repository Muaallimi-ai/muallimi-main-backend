using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.DownstreamEvents;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T115 (US6) — Downstream <c>streak_changed</c> and <c>badge_awarded</c>
/// outbox emission.
///
/// Each ingestion-driven badge or streak transition MUST enqueue exactly
/// one outbox row per change, with the contract-pinned payload shape
/// (see <c>contracts/phase4-downstream-events-contract.md</c>):
///   - streak_changed → { prior_length, new_length, event, family_timezone }
///   - badge_awarded  → { badge_award_id, badge_key, badge_criterion_version, awarded_at }
///
/// The test seeds a 7-day qualifying run, which both pushes the streak up
/// to length 7 (firing 7 streak_changed events) and triggers the v1
/// <c>consistency_7_day_streak</c> badge (firing exactly one badge_awarded).
/// </summary>
public class BadgeStreakDownstreamEventsTests
{
    [Fact]
    public async Task SevenDayRun_Emits_StreakChanged_Per_Day_And_BadgeAwarded_Once()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        var day0 = new DateTime(2026, 03, 01, 09, 00, 00, DateTimeKind.Utc);
        for (var i = 0; i < 7; i++)
        {
            var env = Phase4PipelineHarness.BuildEnvelope(
                $"evt-streak-day-{i}",
                tenantId,
                studentId,
                "lesson_view",
                day0.AddDays(i),
                subjectId,
                topicId);
            await harness.IngestAsync(env);
        }

        await using var db = harness.NewDb();
        var streakEvents = await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                        && e.StudentId == studentId
                        && e.EventKind == nameof(Phase4DownstreamEventKind.streak_changed))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();
        Assert.Equal(7, streakEvents.Count);

        var lastStreak = streakEvents.Last();
        using (var doc = JsonDocument.Parse(lastStreak.Payload))
        {
            var root = doc.RootElement;
            Assert.Equal(7, root.GetProperty("new_length").GetInt32());
            Assert.Equal(6, root.GetProperty("prior_length").GetInt32());
            Assert.Equal("incremented", root.GetProperty("event").GetString());
            Assert.Equal("Asia/Dubai", root.GetProperty("family_timezone").GetString());
        }

        var badgeEvents = await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                        && e.StudentId == studentId
                        && e.EventKind == nameof(Phase4DownstreamEventKind.badge_awarded))
            .ToListAsync();
        Assert.Single(badgeEvents);

        using (var doc = JsonDocument.Parse(badgeEvents[0].Payload))
        {
            var root = doc.RootElement;
            Assert.Equal("consistency_7_day_streak", root.GetProperty("badge_key").GetString());
            Assert.Equal("v1", root.GetProperty("badge_criterion_version").GetString());
            Assert.True(root.TryGetProperty("badge_award_id", out _));
            Assert.True(root.TryGetProperty("awarded_at", out _));
        }

        // All emitted rows must carry the same correlation id as the
        // originating ingestion call (downstream tracing chain).
        Assert.All(streakEvents, e => Assert.False(string.IsNullOrWhiteSpace(e.CorrelationId)));
        Assert.All(badgeEvents, e => Assert.False(string.IsNullOrWhiteSpace(e.CorrelationId)));
    }

    [Fact]
    public async Task StreakReset_EmitsResetEvent_With_NewLength_Zero()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        // Build current_length = 2, then a single event five days later
        // (gap > 1 day) that becomes the new trailing run of length 1.
        var dayA = new DateTime(2026, 04, 01, 09, 00, 00, DateTimeKind.Utc);
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-reset-1", tenantId, studentId, "lesson_view", dayA, subjectId, topicId));
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-reset-2", tenantId, studentId, "lesson_view", dayA.AddDays(1), subjectId, topicId));
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-reset-3", tenantId, studentId, "lesson_view", dayA.AddDays(6), subjectId, topicId));

        await using var db = harness.NewDb();
        var streakEvents = await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                        && e.EventKind == nameof(Phase4DownstreamEventKind.streak_changed))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();

        // After the gap, current_length drops from 2 to 1 — the third
        // event emits a reset transition.
        var resetEvent = streakEvents.Last();
        using var doc = JsonDocument.Parse(resetEvent.Payload);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("prior_length").GetInt32());
        Assert.Equal(1, root.GetProperty("new_length").GetInt32());
        Assert.Equal("reset", root.GetProperty("event").GetString());
    }
}
