using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.ProgressIngestion;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T030 (US4) — Out-of-order delivery convergence.
///
/// The broker offers at-least-once delivery but does NOT guarantee per-partition
/// ordering across student ids. The Phase 4 pipeline MUST converge to the same
/// <c>MasteryState</c> and <c>StreakState</c> regardless of ingestion order,
/// because every calculator is a pure function of the stored
/// <see cref="Muallimi.Domain.Engagement.ProgressRecord"/> set.
///
/// The test picks three events at different timestamps over three days. Ingesting
/// them in-order vs. reverse-order vs. shuffled MUST produce identical state.
/// </summary>
public class ProgressIngestionOutOfOrderTests
{
    [Fact]
    public async Task Mastery_And_Streak_Converge_Under_Any_Delivery_Order()
    {
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var baseDay = new DateTime(2026, 03, 02, 10, 00, 00, DateTimeKind.Utc);
        var envA = Phase4PipelineHarness.BuildEnvelope(
            "evt-A", tenantId, studentId, "lesson_view", baseDay.AddDays(0),
            subjectId, topicId, new { });
        var envB = Phase4PipelineHarness.BuildEnvelope(
            "evt-B", tenantId, studentId, "quiz_answered", baseDay.AddDays(1),
            subjectId, topicId, new { is_correct = true });
        var envC = Phase4PipelineHarness.BuildEnvelope(
            "evt-C", tenantId, studentId, "quiz_answered", baseDay.AddDays(2),
            subjectId, topicId, new { is_correct = false });

        var orderings = new[]
        {
            new[] { envA, envB, envC },
            new[] { envC, envB, envA },
            new[] { envB, envA, envC },
        };

        decimal? expectedScore = null;
        string? expectedBand = null;
        int? expectedStreak = null;
        int? expectedLongest = null;

        foreach (var ordering in orderings)
        {
            var harness = new Phase4PipelineHarness();
            await harness.SeedBadgeCriteriaAsync();
            await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");
            foreach (var e in ordering)
            {
                await harness.IngestAsync(e);
            }

            await using var db = harness.NewDb();
            var mastery = await db.MasteryStates.IgnoreQueryFilters()
                .SingleAsync(m => m.TenantId == tenantId && m.StudentId == studentId);
            var streak = await db.StreakStates.IgnoreQueryFilters()
                .SingleAsync(s => s.TenantId == tenantId && s.StudentId == studentId);

            expectedScore ??= mastery.MasteryScore;
            expectedBand ??= mastery.MasteryBand;
            expectedStreak ??= streak.CurrentLength;
            expectedLongest ??= streak.LongestLength;

            Assert.Equal(expectedScore, mastery.MasteryScore);
            Assert.Equal(expectedBand, mastery.MasteryBand);
            Assert.Equal(expectedStreak, streak.CurrentLength);
            Assert.Equal(expectedLongest, streak.LongestLength);
            Assert.Equal(3, mastery.ContributingRecordCount);
        }

        // Computed mastery from weights: +0.05 (lesson_view) +0.10 (correct) -0.05 (incorrect) = 0.10, band=introduced.
        Assert.Equal(0.10m, expectedScore);
        Assert.Equal(MasteryCalculator.BandIntroduced, expectedBand);
        // Three consecutive qualifying days → current_length = 3, longest = 3.
        Assert.Equal(3, expectedStreak);
        Assert.Equal(3, expectedLongest);
    }
}
