using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Engagement.BadgeAwarding;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Api.Engagement.StreakCalculation;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// Builds a wired-up <see cref="ProgressIngestionWorker"/> against an
/// in-memory <see cref="MuallimiDbContext"/>. Seeds the v1 badge catalogue
/// so badge evaluation has something to grade against.
///
/// Every helper call returns a fresh DbContext against the same database
/// name so that multiple transactional units of work inside one test can
/// simulate the production worker lifecycle (one scope per event).
/// </summary>
internal sealed class Phase4PipelineHarness
{
    public string DatabaseName { get; }

    public Phase4PipelineHarness(string? databaseName = null)
    {
        DatabaseName = databaseName ?? $"phase4-us4-{Guid.NewGuid():N}";
    }

    public MuallimiDbContext NewDb()
    {
        var options = Phase4TestDbContextFactory.BuildOptions(DatabaseName);
        return new Phase4TestDbContext(options);
    }

    public async Task SeedBadgeCriteriaAsync()
    {
        await using var db = NewDb();
        db.BadgeCriteria.AddRange(
            new BadgeCriterion
            {
                BadgeCriterionId = Guid.Parse("b0000001-0000-0000-0000-000000000001"),
                BadgeKey = "consistency_7_day_streak",
                Version = "v1",
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
                Version = "v1",
                Category = "accuracy",
                DisplayNameAr = "دقّة ٨٠٪",
                DisplayNameEn = "80% Accuracy",
                DescriptionAr = "حصل على ٨٠٪ أو أكثر في اختبار عشرين سؤالًا.",
                DescriptionEn = "Scored 80% or higher on a twenty-question quiz.",
                Threshold = "{\"type\":\"quiz_accuracy\",\"min_correct_pct\":0.8,\"min_questions\":20}",
            });
        await db.SaveChangesAsync();
    }

    public async Task SeedParentTimezoneAsync(Guid tenantId, Guid studentId, string timezone)
    {
        await using var db = NewDb();
        var parentId = Guid.NewGuid();
        db.ParentProfiles.Add(new Muallimi.Domain.Parents.ParentProfile
        {
            ParentProfileId = parentId,
            TenantId = tenantId,
            IdentityId = Guid.NewGuid(),
            PreferredLanguage = "ar",
            Locale = "ar-SA",
            Timezone = timezone,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.ChildLinks.Add(new Muallimi.Domain.Parents.ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId,
            ParentProfileId = parentId,
            StudentId = studentId,
            Role = "guardian",
            EffectiveStart = DateTime.UtcNow.Date.AddDays(-30),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public async Task<ProgressIngestionOutcome> IngestAsync(Phase3EventEnvelope envelope)
    {
        await using var db = NewDb();
        var worker = BuildWorker(db);
        return await worker.ProcessAsync(envelope);
    }

    public ProgressIngestionWorker BuildWorker(MuallimiDbContext db)
    {
        var records = new ProgressRecordRepository(db);
        var masteryStates = new MasteryStateRepository(db);
        var streakStates = new StreakStateRepository(db);
        var criteria = new BadgeCriterionRepository(db);
        var awards = new BadgeAwardRepository(db);

        var timezone = new FamilyTimezoneResolver(db);
        var mastery = new MasteryCalculator(records, masteryStates);
        var streak = new StreakCalculator(records, streakStates, timezone);
        var badges = new BadgeEvaluator(criteria, awards, records);
        var outbox = new Phase4DownstreamEventOutbox(db);
        var emitter = new Phase4DownstreamEventEmitter(outbox);
        var deadLetter = new ProgressIngestionDeadLetterStore(db);

        return new ProgressIngestionWorker(
            db, records, mastery, masteryStates, streak, badges, emitter, deadLetter,
            NullLogger<ProgressIngestionWorker>.Instance);
    }

    public static Phase3EventEnvelope BuildEnvelope(
        string sourceEventId,
        Guid tenantId,
        Guid studentId,
        string kind,
        DateTime occurredAtUtc,
        Guid? subjectId = null,
        Guid? topicId = null,
        object? payload = null,
        string? correlationId = null,
        string curriculumType = "moe")
    {
        var scope = new
        {
            curriculum_type = curriculumType,
            grade = 7,
            subject_id = subjectId?.ToString(),
            topic_id = topicId?.ToString(),
        };
        return new Phase3EventEnvelope
        {
            SourceEventId = sourceEventId,
            EventKind = kind,
            TenantId = tenantId,
            StudentId = studentId,
            CorrelationId = correlationId ?? Guid.NewGuid().ToString("D"),
            OccurredAt = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc),
            CurriculumScope = JsonSerializer.SerializeToElement(scope),
            Payload = payload is null ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.SerializeToElement(payload),
        };
    }
}
