using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.FocusAreas;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.ProgressIngestion;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// Harness for US5 focus-area integration tests. Wires a
/// <see cref="FocusAreaCalculator"/> against an in-memory
/// <see cref="MuallimiDbContext"/> with:
///   - a scripted tutor runtime client so the Phase 2 guardrail chain pass-
///     through can be asserted without the tutor runtime,
///   - a scripted curriculum retrieval client so deep-link validation can be
///     exercised without the Phase 1 retrieval surface,
///   - helpers to seed progress records + mastery state.
/// </summary>
internal sealed class FocusAreaTestHarness
{
    public MuallimiDbContext Db { get; }
    public ScriptedTutorRuntimeClient Tutor { get; }
    public ScriptedCurriculumRetrievalClient Curriculum { get; }
    public IFocusAreaRepository Repository { get; }
    public IFocusAreaCalculator Calculator { get; }

    public FocusAreaTestHarness(MuallimiDbContext? db = null)
    {
        Db = db ?? Phase4TestDbContextFactory.Create();
        Tutor = new ScriptedTutorRuntimeClient();
        Curriculum = new ScriptedCurriculumRetrievalClient();
        Repository = new FocusAreaRepository(Db);

        var trails = new GuardrailDecisionTrailStore(Db);
        var signals = new FocusAreaSignalCollector(Db);
        var deepLinks = new FocusAreaDeepLinkValidator(Curriculum);
        var rationales = new FocusAreaRationaleGenerator(Tutor, trails);
        var outbox = new Phase4DownstreamEventOutbox(Db);
        var emitter = new FocusAreaEventEmitter(outbox);

        Calculator = new FocusAreaCalculator(
            Db, Repository, signals, deepLinks, rationales, emitter,
            NullLogger<FocusAreaCalculator>.Instance);
    }

    public async Task SeedQuizErrorAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid chapterId,
        Guid topicId,
        int errorCount = 3,
        string curriculumType = "moe",
        string correlationId = "corr-fa-seed")
    {
        var scope = JsonSerializer.Serialize(new
        {
            curriculum_type = curriculumType,
            grade = 7,
            subject_id = subjectId,
            chapter_id = chapterId,
            topic_id = topicId,
        });

        for (var i = 0; i < errorCount; i++)
        {
            Db.ProgressRecords.Add(new ProgressRecord
            {
                ProgressRecordId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                SourceEventId = $"evt-{Guid.NewGuid():N}",
                EventKind = Phase3EventKinds.QuizAnswered,
                CurriculumScope = scope,
                Payload = JsonSerializer.Serialize(new { is_correct = false }),
                CorrelationId = correlationId,
                OccurredAt = DateTime.UtcNow.AddHours(-i),
                IngestedAt = DateTime.UtcNow,
            });
        }

        Db.MasteryStates.Add(new MasteryState
        {
            MasteryStateId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            CurriculumType = curriculumType,
            SubjectId = subjectId,
            TopicId = topicId,
            MasteryScore = 0.2m,
            MasteryBand = MasteryCalculator.BandIntroduced,
            CalculationVersion = MasteryCalculator.Version,
            ContributingRecordCount = errorCount,
            LastUpdatedAt = DateTime.UtcNow,
            LastCorrelationId = correlationId,
        });

        await Db.SaveChangesAsync();
    }

    public async Task SeedConfidentTopicAsync(
        Guid tenantId,
        Guid studentId,
        Guid subjectId,
        Guid chapterId,
        Guid topicId,
        string curriculumType = "moe",
        string correlationId = "corr-fa-confident")
    {
        var scope = JsonSerializer.Serialize(new
        {
            curriculum_type = curriculumType,
            grade = 7,
            subject_id = subjectId,
            chapter_id = chapterId,
            topic_id = topicId,
        });

        Db.ProgressRecords.Add(new ProgressRecord
        {
            ProgressRecordId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            SourceEventId = $"evt-{Guid.NewGuid():N}",
            EventKind = Phase3EventKinds.QuizAnswered,
            CurriculumScope = scope,
            Payload = JsonSerializer.Serialize(new { is_correct = true }),
            CorrelationId = correlationId,
            OccurredAt = DateTime.UtcNow,
            IngestedAt = DateTime.UtcNow,
        });

        Db.MasteryStates.Add(new MasteryState
        {
            MasteryStateId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = studentId,
            CurriculumType = curriculumType,
            SubjectId = subjectId,
            TopicId = topicId,
            MasteryScore = 0.85m,
            MasteryBand = MasteryCalculator.BandConfident,
            CalculationVersion = MasteryCalculator.Version,
            ContributingRecordCount = 1,
            LastUpdatedAt = DateTime.UtcNow,
            LastCorrelationId = correlationId,
        });

        await Db.SaveChangesAsync();
    }
}

internal sealed class ScriptedCurriculumRetrievalClient : IPhase4CurriculumRetrievalClient
{
    public List<(Guid SubjectId, Guid ChapterId, Guid TopicId)> Calls { get; } = new();
    public HashSet<(Guid SubjectId, Guid ChapterId, Guid TopicId)> UnknownNodes { get; } = new();
    public string DefaultStatus { get; set; } = "approved";
    public string DefaultPath { get; set; } = "/phase1/approved-node";

    public Task<CurriculumNodeResolution> ResolveNodeAsync(
        Guid subjectId,
        Guid chapterId,
        Guid topicId,
        CancellationToken ct = default)
    {
        Calls.Add((subjectId, chapterId, topicId));
        if (UnknownNodes.Contains((subjectId, chapterId, topicId)))
        {
            return Task.FromResult(new CurriculumNodeResolution(
                Exists: false,
                Path: null,
                Status: "not_found",
                LessonIds: Array.Empty<Guid>()));
        }

        return Task.FromResult(new CurriculumNodeResolution(
            Exists: true,
            Path: DefaultPath,
            Status: DefaultStatus,
            LessonIds: Array.Empty<Guid>()));
    }
}
