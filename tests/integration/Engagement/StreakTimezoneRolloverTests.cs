using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Api.Engagement.StreakCalculation;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T113 (US6) — Streak timezone-rollover handling.
///
/// Streak day-bucketing is authoritative in the family's IANA timezone
/// (FR-005 + FamilyTimezoneResolver). A pair of UTC events that straddle
/// midnight UTC but land on the same calendar day in the family's timezone
/// MUST count as ONE qualifying day, not two. Symmetrically, two events
/// inside the same UTC day that span the family-timezone midnight rollover
/// MUST count as TWO qualifying days.
///
/// The test covers both directions:
///   - America/Los_Angeles (UTC-8): 23:30 UTC + 02:00 UTC next-day land
///     on the same family day → current_length = 1.
///   - Asia/Tokyo (UTC+9): 14:30 UTC + 16:00 UTC same-UTC-day cross
///     local midnight → current_length = 2.
/// </summary>
public class StreakTimezoneRolloverTests
{
    [Fact]
    public async Task UtcMidnightCrossing_DoesNotInflate_StreakInWesternTimezone()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "America/Los_Angeles");

        // 23:30 UTC and 02:00 UTC the next day. Pacific Time is UTC-8 in
        // March (or UTC-7 in DST); both events land on 2026-03-04 locally.
        var firstUtc = new DateTime(2026, 03, 04, 23, 30, 00, DateTimeKind.Utc);
        var secondUtc = new DateTime(2026, 03, 05, 02, 00, 00, DateTimeKind.Utc);

        var first = Phase4PipelineHarness.BuildEnvelope(
            "evt-tz-west-1", tenantId, studentId, "lesson_view", firstUtc,
            subjectId, topicId, new { });
        var second = Phase4PipelineHarness.BuildEnvelope(
            "evt-tz-west-2", tenantId, studentId, "quiz_answered", secondUtc,
            subjectId, topicId, new { is_correct = true });

        Assert.Equal(ProgressIngestionOutcome.Inserted, await harness.IngestAsync(first));
        Assert.Equal(ProgressIngestionOutcome.Inserted, await harness.IngestAsync(second));

        await using var db = harness.NewDb();
        var streak = await db.StreakStates.IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == tenantId && s.StudentId == studentId);

        Assert.Equal(1, streak.CurrentLength);
        Assert.Equal(1, streak.LongestLength);
        Assert.Equal("America/Los_Angeles", streak.FamilyTimezone);
    }

    [Fact]
    public async Task EasternTimezone_LocalMidnightCrossing_AddsTwoDaysToStreak()
    {
        var harness = new Phase4PipelineHarness();
        await harness.SeedBadgeCriteriaAsync();

        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Tokyo");

        // Tokyo is UTC+9. 14:30 UTC = 23:30 local; 16:00 UTC = 01:00 local
        // next day. Same UTC date but two distinct family-local days.
        var beforeLocalMidnight = new DateTime(2026, 03, 04, 14, 30, 00, DateTimeKind.Utc);
        var afterLocalMidnight = new DateTime(2026, 03, 04, 16, 00, 00, DateTimeKind.Utc);

        var first = Phase4PipelineHarness.BuildEnvelope(
            "evt-tz-east-1", tenantId, studentId, "lesson_view", beforeLocalMidnight,
            subjectId, topicId, new { });
        var second = Phase4PipelineHarness.BuildEnvelope(
            "evt-tz-east-2", tenantId, studentId, "quiz_answered", afterLocalMidnight,
            subjectId, topicId, new { is_correct = true });

        Assert.Equal(ProgressIngestionOutcome.Inserted, await harness.IngestAsync(first));
        Assert.Equal(ProgressIngestionOutcome.Inserted, await harness.IngestAsync(second));

        await using var db = harness.NewDb();
        var streak = await db.StreakStates.IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == tenantId && s.StudentId == studentId);

        Assert.Equal(2, streak.CurrentLength);
        Assert.Equal(2, streak.LongestLength);
        Assert.Equal("Asia/Tokyo", streak.FamilyTimezone);
    }

    [Fact]
    public void CalendarDay_Helper_RespectsFamilyTimezone()
    {
        // Defensive unit-level check on the resolver's static helper, since
        // the streak result is only as correct as this conversion.
        var lateUtc = new DateTime(2026, 03, 04, 23, 30, 00, DateTimeKind.Utc);
        var pacificDay = FamilyTimezoneResolver.CalendarDay(lateUtc, "America/Los_Angeles");
        var tokyoDay = FamilyTimezoneResolver.CalendarDay(lateUtc, "Asia/Tokyo");

        Assert.Equal(new DateOnly(2026, 03, 04), pacificDay);
        Assert.Equal(new DateOnly(2026, 03, 05), tokyoDay);
    }
}
