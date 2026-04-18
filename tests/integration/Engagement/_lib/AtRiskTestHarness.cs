using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Engagement.AtRiskDetection;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.FocusAreas;
using Muallimi.Api.Engagement.InterventionPrompts;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// Harness for US8 at-risk integration tests. Wires the
/// <see cref="AtRiskDetectionOrchestrator"/> against an in-memory
/// <see cref="MuallimiDbContext"/> with a scripted tutor runtime client and
/// a scripted curriculum retrieval client so the Phase 2 guardrail chain
/// pass-through and the Phase 1 deep-link grounding can be asserted without
/// the upstream services running.
/// </summary>
internal sealed class AtRiskTestHarness
{
    public MuallimiDbContext Db { get; }
    public ScriptedTutorRuntimeClient Tutor { get; }
    public ScriptedCurriculumRetrievalClient Curriculum { get; }
    public AtRiskThresholdCatalogue Catalogue { get; }
    public IAtRiskFlagRepository Flags { get; }
    public IInterventionPromptRepository Prompts { get; }
    public AtRiskDetectionOrchestrator Orchestrator { get; }

    public AtRiskTestHarness(MuallimiDbContext? db = null, AtRiskThresholdSet? thresholds = null)
    {
        Db = db ?? Phase4TestDbContextFactory.Create();
        Tutor = new ScriptedTutorRuntimeClient();
        Curriculum = new ScriptedCurriculumRetrievalClient();
        Catalogue = new AtRiskThresholdCatalogue(thresholds ?? AtRiskThresholdCatalogue.Default());
        Flags = new AtRiskFlagRepository(Db);
        Prompts = new InterventionPromptRepository(Db);

        var trails = new GuardrailDecisionTrailStore(Db);
        var evaluator = new AtRiskEvaluator(Db);
        var generator = new InterventionPromptGenerator(Tutor, trails);
        var deepLinks = new FocusAreaDeepLinkValidator(Curriculum);
        var outbox = new Phase4DownstreamEventOutbox(Db);
        var emitter = new AtRiskEventEmitter(outbox);

        Orchestrator = new AtRiskDetectionOrchestrator(
            Db, Flags, Prompts, evaluator, Catalogue, generator, deepLinks, emitter,
            NullLogger<AtRiskDetectionOrchestrator>.Instance);
    }

    public async Task SeedSustainedLowMasteryAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid topicId,
        decimal masteryScore = 0.20m,
        int contributingRecords = 8,
        string curriculumType = "moe")
    {
        Db.MasteryStates.Add(new MasteryState
        {
            MasteryStateId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            CurriculumType = curriculumType,
            SubjectId = subjectId,
            TopicId = topicId,
            MasteryScore = masteryScore,
            MasteryBand = MasteryCalculator.BandIntroduced,
            CalculationVersion = MasteryCalculator.Version,
            ContributingRecordCount = contributingRecords,
            LastUpdatedAt = DateTime.UtcNow,
            LastCorrelationId = "corr-atrisk-mastery",
        });
        await Db.SaveChangesAsync();
    }

    public async Task SeedRecoveryMasteryAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid topicId,
        decimal masteryScore = 0.75m,
        string curriculumType = "moe")
    {
        var existing = await Db.MasteryStates
            .Where(m => m.TenantId == tenantId && m.StudentId == studentId && m.TopicId == topicId)
            .ToListAsync();
        Db.MasteryStates.RemoveRange(existing);

        Db.MasteryStates.Add(new MasteryState
        {
            MasteryStateId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            CurriculumType = curriculumType,
            SubjectId = subjectId,
            TopicId = topicId,
            MasteryScore = masteryScore,
            MasteryBand = MasteryCalculator.BandConfident,
            CalculationVersion = MasteryCalculator.Version,
            ContributingRecordCount = 4,
            LastUpdatedAt = DateTime.UtcNow,
            LastCorrelationId = "corr-atrisk-recovery",
        });
        await Db.SaveChangesAsync();
    }

    public async Task SeedPassingMockTestAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid topicId)
    {
        var scope = JsonSerializer.Serialize(new
        {
            curriculum_type = "moe",
            subject_id = subjectId,
            chapter_id = Guid.NewGuid(),
            topic_id = topicId,
        });
        Db.ProgressRecords.Add(new ProgressRecord
        {
            ProgressRecordId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            SourceEventId = $"evt-{Guid.NewGuid():N}",
            EventKind = Phase3EventKinds.MockTest,
            CurriculumScope = scope,
            Payload = JsonSerializer.Serialize(new { passed = true }),
            CorrelationId = "corr-atrisk-mock-pass",
            OccurredAt = DateTime.UtcNow,
            IngestedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();
    }

    public async Task SeedConfidentTopicAsync(
        Guid tenantId,
        Guid studentId,
        decimal masteryScore = 0.80m)
    {
        Db.MasteryStates.Add(new MasteryState
        {
            MasteryStateId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            CurriculumType = "moe",
            SubjectId = Guid.NewGuid(),
            TopicId = Guid.NewGuid(),
            MasteryScore = masteryScore,
            MasteryBand = MasteryCalculator.BandConfident,
            CalculationVersion = MasteryCalculator.Version,
            ContributingRecordCount = 3,
            LastUpdatedAt = DateTime.UtcNow,
            LastCorrelationId = "corr-atrisk-confident",
        });
        await Db.SaveChangesAsync();
    }
}
