using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Engagement.BadgeAwarding;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T114 (US6) — Badge criterion versioning safety.
///
/// Asserts that updating the catalogue is strictly additive:
/// 1. Inserting a new (badge_key, version) pair never re-awards a badge to
///    a student who already holds an older version of the same key — past
///    awards remain valid and pinned to their original version.
/// 2. The additive loader refuses to overwrite the threshold or category
///    of an existing (badge_key, version) row, since BadgeAward rows pin
///    to that exact pair (FR-013, FR-014).
/// 3. Re-running the loader is idempotent — second invocation inserts zero
///    rows and does not double-award any pre-existing badges on re-evaluation.
/// </summary>
public class BadgeCriterionVersionTests
{
    [Fact]
    public async Task NewVersion_DoesNotInvalidate_OrDoubleAward_PreviousVersion()
    {
        var harness = new Phase4PipelineHarness();
        var tenantId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        await harness.SeedParentTimezoneAsync(tenantId, studentId, "Asia/Dubai");

        // Seed only the v1 streak criterion (3-day) and award it.
        var criterionId = Guid.Parse("c1100001-0000-0000-0000-000000000001");
        await using (var db = harness.NewDb())
        {
            db.BadgeCriteria.Add(new BadgeCriterion
            {
                BadgeCriterionId = criterionId,
                BadgeKey = "consistency_3_day_streak",
                Version = "v1",
                Category = "consistency",
                DisplayNameAr = "ثلاث أيام",
                DisplayNameEn = "Three-day Streak",
                Threshold = "{\"type\":\"streak\",\"days\":3}",
            });
            await db.SaveChangesAsync();
        }

        // Three consecutive days of activity → trigger the v1 award.
        var day1 = new DateTime(2026, 03, 01, 10, 00, 00, DateTimeKind.Utc);
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-day-1", tenantId, studentId, "lesson_view", day1, subjectId, topicId));
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-day-2", tenantId, studentId, "lesson_view", day1.AddDays(1), subjectId, topicId));
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-day-3", tenantId, studentId, "quiz_answered", day1.AddDays(2), subjectId, topicId,
            new { is_correct = true }));

        await using (var db = harness.NewDb())
        {
            var awards = await db.BadgeAwards.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.StudentId == studentId)
                .ToListAsync();
            Assert.Single(awards);
            Assert.Equal("v1", awards[0].BadgeCriterionVersion);
            Assert.Equal(criterionId, awards[0].BadgeCriterionId);
        }

        // Loader-driven catalogue update: introduce a v2 of the same key
        // with a stricter threshold (5 days) — the student does not yet
        // qualify for v2 and v1 is left untouched.
        var loader = await NewLoaderAsync(harness);
        var result = await loader.LoadAsync(new[]
        {
            new BadgeCriterion
            {
                BadgeCriterionId = Guid.Parse("c1100002-0000-0000-0000-000000000002"),
                BadgeKey = "consistency_3_day_streak",
                Version = "v2",
                Category = "consistency",
                DisplayNameAr = "خمس أيام",
                DisplayNameEn = "Five-day Streak",
                Threshold = "{\"type\":\"streak\",\"days\":5}",
            },
        });
        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.RejectedMutation);

        // Re-evaluate after a fourth day of activity. The student still
        // does NOT qualify for v2 (5-day threshold), and the existing v1
        // award MUST NOT be duplicated.
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-day-4", tenantId, studentId, "lesson_view", day1.AddDays(3), subjectId, topicId));

        await using (var db = harness.NewDb())
        {
            var awards = await db.BadgeAwards.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.StudentId == studentId)
                .ToListAsync();
            Assert.Single(awards);
            Assert.Equal("v1", awards[0].BadgeCriterionVersion);
        }

        // Fifth qualifying day → student now satisfies v2; v1 award is
        // preserved and v2 is added as a separate row.
        await harness.IngestAsync(Phase4PipelineHarness.BuildEnvelope(
            "evt-day-5", tenantId, studentId, "quiz_answered", day1.AddDays(4), subjectId, topicId,
            new { is_correct = true }));

        await using (var db = harness.NewDb())
        {
            var awards = await db.BadgeAwards.IgnoreQueryFilters()
                .Where(b => b.TenantId == tenantId && b.StudentId == studentId)
                .OrderBy(b => b.BadgeCriterionVersion)
                .ToListAsync();
            Assert.Equal(2, awards.Count);
            Assert.Equal(new[] { "v1", "v2" }, awards.Select(a => a.BadgeCriterionVersion).ToArray());
        }
    }

    [Fact]
    public async Task Loader_RefusesMutation_OfExistingKeyVersion_AndIsIdempotent()
    {
        var harness = new Phase4PipelineHarness();
        var loader = await NewLoaderAsync(harness);

        var seedV1 = new BadgeCriterion
        {
            BadgeCriterionId = Guid.Parse("c2200001-0000-0000-0000-000000000001"),
            BadgeKey = "accuracy_quick",
            Version = "v1",
            Category = "accuracy",
            DisplayNameAr = "دقّة سريعة",
            DisplayNameEn = "Quick Accuracy",
            Threshold = "{\"type\":\"quiz_accuracy\",\"min_correct_pct\":0.7,\"min_questions\":5}",
        };

        var first = await loader.LoadAsync(new[] { seedV1 });
        Assert.Equal(1, first.Inserted);
        Assert.Equal(0, first.SkippedExisting);
        Assert.Equal(0, first.RejectedMutation);

        // Re-running with the identical seed is a no-op (additive loader).
        var second = await loader.LoadAsync(new[] { seedV1 });
        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.SkippedExisting);

        // Attempt to mutate the same (key, version) pair: must be rejected.
        var mutated = new BadgeCriterion
        {
            BadgeCriterionId = seedV1.BadgeCriterionId,
            BadgeKey = "accuracy_quick",
            Version = "v1",
            Category = "accuracy",
            DisplayNameAr = "دقّة سريعة",
            DisplayNameEn = "Quick Accuracy",
            Threshold = "{\"type\":\"quiz_accuracy\",\"min_correct_pct\":0.95,\"min_questions\":5}",
        };
        var mutation = await loader.LoadAsync(new[] { mutated });
        Assert.Equal(0, mutation.Inserted);
        Assert.Equal(1, mutation.RejectedMutation);

        // Database still holds the original threshold — no silent rewrite.
        await using var db = harness.NewDb();
        var stored = await db.BadgeCriteria.IgnoreQueryFilters()
            .SingleAsync(c => c.BadgeKey == "accuracy_quick" && c.Version == "v1");
        Assert.Contains("\"min_correct_pct\":0.7", stored.Threshold);
    }

    private static async Task<IBadgeCriterionCatalogueLoader> NewLoaderAsync(Phase4PipelineHarness harness)
    {
        await Task.CompletedTask;
        var db = harness.NewDb();
        return new BadgeCriterionCatalogueLoader(
            db, NullLogger<BadgeCriterionCatalogueLoader>.Instance);
    }
}
