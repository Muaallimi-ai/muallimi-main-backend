using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.Seeds;

/// <summary>
/// T018 — Badge criterion catalogue v1.
///
/// Seeds the four Phase 4 badge categories with Arabic and English display
/// strings and versioned thresholds. Additive-only: new criteria in future
/// versions are new rows; existing rows are retired (not mutated) once any
/// award references them.
/// </summary>
public static class BadgeCriterionV1
{
    public const string Version = "v1";

    public static IReadOnlyList<BadgeCriterion> All => new[]
    {
        new BadgeCriterion
        {
            BadgeCriterionId = Guid.Parse("b0000001-0000-0000-0000-000000000001"),
            BadgeKey = "consistency_7_day_streak",
            Version = Version,
            Category = "consistency",
            DisplayNameAr = "مواظب أسبوع",
            DisplayNameEn = "Week-long Streak",
            DescriptionAr = "سبعة أيام متتالية من الدراسة اليومية.",
            DescriptionEn = "Seven consecutive days of daily study.",
            Threshold = "{\"type\":\"streak\",\"days\":7}",
        },
        new BadgeCriterion
        {
            BadgeCriterionId = Guid.Parse("b0000002-0000-0000-0000-000000000002"),
            BadgeKey = "accuracy_80_quiz",
            Version = Version,
            Category = "accuracy",
            DisplayNameAr = "دقّة ٨٠٪",
            DisplayNameEn = "80% Accuracy",
            DescriptionAr = "حصل على ٨٠٪ أو أكثر في اختبار عشرين سؤالًا.",
            DescriptionEn = "Scored 80% or higher on a twenty-question quiz.",
            Threshold = "{\"type\":\"quiz_accuracy\",\"min_correct_pct\":0.8,\"min_questions\":20}",
        },
        new BadgeCriterion
        {
            BadgeCriterionId = Guid.Parse("b0000003-0000-0000-0000-000000000003"),
            BadgeKey = "coverage_topic_full_pass",
            Version = Version,
            Category = "coverage",
            DisplayNameAr = "إتقان موضوع",
            DisplayNameEn = "Topic Mastered",
            DescriptionAr = "غطّى جميع دروس الموضوع في الأسبوع.",
            DescriptionEn = "Completed every lesson in the topic this week.",
            Threshold = "{\"type\":\"topic_coverage\",\"completion_pct\":1.0}",
        },
        new BadgeCriterion
        {
            BadgeCriterionId = Guid.Parse("b0000004-0000-0000-0000-000000000004"),
            BadgeKey = "improvement_mastery_jump",
            Version = Version,
            Category = "improvement",
            DisplayNameAr = "قفزة تقدّم",
            DisplayNameEn = "Mastery Leap",
            DescriptionAr = "ارتقى مستواه في الموضوع بمستوى واحد خلال أسبوع.",
            DescriptionEn = "Moved up one mastery band in a single week.",
            Threshold = "{\"type\":\"mastery_band_jump\",\"bands\":1}",
        },
    };

    public static async Task SeedAsync(MuallimiDbContext db, CancellationToken ct = default)
    {
        var seeds = All;
        foreach (var seed in seeds)
        {
            var existing = await db.BadgeCriteria
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.BadgeKey == seed.BadgeKey && c.Version == seed.Version, ct);
            if (existing is null)
            {
                db.BadgeCriteria.Add(seed);
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
