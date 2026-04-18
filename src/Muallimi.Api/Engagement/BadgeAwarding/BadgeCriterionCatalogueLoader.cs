using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.BadgeAwarding;

// T119 (US6) — Additive badge catalogue loader.
//
// Reconciles the in-process seed catalogue (e.g. BadgeCriterionV1) with the
// BadgeCriterion table on startup. The loader is strictly additive: a new
// (BadgeKey, Version) pair is inserted; an existing pair is never mutated.
// This guarantees that any historical BadgeAward continues to point at the
// exact criterion row it was awarded against (FR-013, FR-014).
//
// Retiring a criterion is handled by setting RetiredAt on the existing row
// out-of-band — the loader never overwrites RetiredAt either.
public interface IBadgeCriterionCatalogueLoader
{
    Task<BadgeCatalogueLoadResult> LoadAsync(IReadOnlyList<BadgeCriterion> seeds, CancellationToken ct = default);
}

public sealed record BadgeCatalogueLoadResult(int Inserted, int SkippedExisting, int RejectedMutation)
{
    public int TotalConsidered => Inserted + SkippedExisting + RejectedMutation;
}

public sealed class BadgeCriterionCatalogueLoader : IBadgeCriterionCatalogueLoader
{
    private readonly MuallimiDbContext _db;
    private readonly ILogger<BadgeCriterionCatalogueLoader> _logger;

    public BadgeCriterionCatalogueLoader(
        MuallimiDbContext db,
        ILogger<BadgeCriterionCatalogueLoader> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<BadgeCatalogueLoadResult> LoadAsync(
        IReadOnlyList<BadgeCriterion> seeds,
        CancellationToken ct = default)
    {
        if (seeds is null || seeds.Count == 0)
        {
            return new BadgeCatalogueLoadResult(0, 0, 0);
        }

        var existingRows = await _db.BadgeCriteria
            .IgnoreQueryFilters()
            .ToListAsync(ct);
        var byKeyVersion = existingRows.ToDictionary(
            c => (c.BadgeKey, c.Version),
            c => c);

        var inserted = 0;
        var skipped = 0;
        var rejected = 0;

        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.BadgeKey) || string.IsNullOrWhiteSpace(seed.Version))
            {
                rejected++;
                _logger.LogWarning(
                    "BadgeCriterionCatalogueLoader rejected seed with missing key/version: key={Key} version={Version}",
                    seed.BadgeKey, seed.Version);
                continue;
            }

            if (byKeyVersion.TryGetValue((seed.BadgeKey, seed.Version), out var existing))
            {
                if (HasMutatedDefinition(existing, seed))
                {
                    rejected++;
                    _logger.LogWarning(
                        "BadgeCriterionCatalogueLoader refused to mutate existing criterion key={Key} version={Version}; bump the version instead",
                        seed.BadgeKey, seed.Version);
                }
                else
                {
                    skipped++;
                }
                continue;
            }

            _db.BadgeCriteria.Add(new BadgeCriterion
            {
                BadgeCriterionId = seed.BadgeCriterionId == Guid.Empty ? Guid.NewGuid() : seed.BadgeCriterionId,
                BadgeKey = seed.BadgeKey,
                Version = seed.Version,
                Category = seed.Category,
                DisplayNameAr = seed.DisplayNameAr,
                DisplayNameEn = seed.DisplayNameEn,
                DescriptionAr = seed.DescriptionAr,
                DescriptionEn = seed.DescriptionEn,
                Threshold = string.IsNullOrWhiteSpace(seed.Threshold) ? "{}" : seed.Threshold,
                RetiredAt = seed.RetiredAt,
            });
            inserted++;
        }

        if (inserted > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return new BadgeCatalogueLoadResult(inserted, skipped, rejected);
    }

    private static bool HasMutatedDefinition(BadgeCriterion existing, BadgeCriterion seed)
    {
        // Threshold and category drift across the same (key, version) pair
        // would silently rewrite history. The loader treats that as a
        // rejection so the operator must bump the version.
        return existing.Threshold != (string.IsNullOrWhiteSpace(seed.Threshold) ? "{}" : seed.Threshold)
               || existing.Category != seed.Category;
    }
}

public static class BadgeCriterionCatalogueLoaderServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4BadgeCriterionCatalogueLoader(this IServiceCollection services)
    {
        services.AddScoped<IBadgeCriterionCatalogueLoader, BadgeCriterionCatalogueLoader>();
        return services;
    }
}

// Built-in v1 seeds. Mirrors db/seeds/BadgeCriterionV1.cs so the loader is
// self-contained at the API project boundary. New badge versions must be
// added as new (BadgeKey, Version) pairs — never by mutating an existing
// row, since historical BadgeAward rows pin to (criterion_id, version).
public static class BadgeCriterionCatalogueV1
{
    public const string Version = "v1";

    public static IReadOnlyList<BadgeCriterion> Seeds { get; } = new[]
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
}
