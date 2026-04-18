using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Engagement.BadgeAwarding;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.MasteryCalculation;
using Muallimi.Api.Engagement.Observability;
using Muallimi.Api.Engagement.StreakCalculation;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.ProgressIngestion;

/// <summary>
/// T012 + T040 (US4) — Progress ingestion worker.
///
/// One <see cref="ProcessAsync"/> call per Phase 3 session event. Behaviour:
///
/// 1. Reject unknown event kinds → dead-letter with <c>unknown_event_kind</c>.
/// 2. Reject malformed envelopes (empty source_event_id, empty tenant/student
///    guid) → dead-letter with <c>malformed_payload</c> / <c>tenant_not_found</c>
///    / <c>student_not_found</c>.
/// 3. Look up <c>(tenant_id, source_event_id)</c> in <c>ProgressRecord</c>;
///    if present, return <see cref="ProgressIngestionOutcome.Duplicate"/>
///    without state change — this enforces at-least-once delivery safely.
/// 4. Inside a single transactional boundary:
///    - insert the <see cref="ProgressRecord"/> row
///    - recompute <see cref="MasteryState"/> for the touched (subject, topic)
///    - recompute <see cref="StreakState"/> for the student
///    - evaluate badge criteria against the updated mastery / streak
///    - enqueue <c>mastery_updated</c>, <c>streak_changed</c>,
///      <c>badge_awarded</c> downstream outbox rows
///    - <see cref="DbContext.SaveChangesAsync"/> + commit
///
/// The database transaction is a no-op under EF Core InMemory but binds the
/// unit-of-work boundary under PostgreSQL (see research.md for the outbox
/// rationale). Transient SaveChanges failures propagate so the consumer can
/// NACK-requeue; permanent failures end up in the dead-letter store.
/// </summary>
public interface IProgressIngestionWorker
{
    Task<ProgressIngestionOutcome> ProcessAsync(Phase3EventEnvelope envelope, CancellationToken ct = default);
}

public enum ProgressIngestionOutcome
{
    Inserted,
    Duplicate,
    Rejected,
}

public sealed class ProgressIngestionWorker : IProgressIngestionWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;
    private readonly IProgressRecordRepository _records;
    private readonly IMasteryCalculator _mastery;
    private readonly IMasteryStateRepository _masteryStates;
    private readonly IStreakCalculator _streak;
    private readonly IBadgeEvaluator _badges;
    private readonly IPhase4DownstreamEventEmitter _downstream;
    private readonly IProgressIngestionDeadLetterStore _deadLetters;
    private readonly ILogger<ProgressIngestionWorker> _logger;

    public ProgressIngestionWorker(
        MuallimiDbContext db,
        IProgressRecordRepository records,
        IMasteryCalculator mastery,
        IMasteryStateRepository masteryStates,
        IStreakCalculator streak,
        IBadgeEvaluator badges,
        IPhase4DownstreamEventEmitter downstream,
        IProgressIngestionDeadLetterStore deadLetters,
        ILogger<ProgressIngestionWorker> logger)
    {
        _db = db;
        _records = records;
        _mastery = mastery;
        _masteryStates = masteryStates;
        _streak = streak;
        _badges = badges;
        _downstream = downstream;
        _deadLetters = deadLetters;
        _logger = logger;
    }

    public async Task<ProgressIngestionOutcome> ProcessAsync(Phase3EventEnvelope envelope, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.SourceEventId))
        {
            if (envelope is not null)
            {
                await _deadLetters.RecordAsync(envelope, ProgressIngestionDeadLetterReasons.MalformedPayload, ct);
                Phase4Metrics.IngestionDeadLettered.Add(1);
            }
            return ProgressIngestionOutcome.Rejected;
        }

        if (!Phase3EventKinds.All.Contains(envelope.EventKind))
        {
            await _deadLetters.RecordAsync(envelope, ProgressIngestionDeadLetterReasons.UnknownEventKind, ct);
            Phase4Metrics.IngestionDeadLettered.Add(1);
            _logger.LogWarning("ProgressIngestionWorker rejected unknown kind={Kind} source={Src}", envelope.EventKind, envelope.SourceEventId);
            return ProgressIngestionOutcome.Rejected;
        }

        if (envelope.TenantId == Guid.Empty)
        {
            await _deadLetters.RecordAsync(envelope, ProgressIngestionDeadLetterReasons.TenantNotFound, ct);
            Phase4Metrics.IngestionDeadLettered.Add(1);
            return ProgressIngestionOutcome.Rejected;
        }
        if (envelope.StudentId == Guid.Empty)
        {
            await _deadLetters.RecordAsync(envelope, ProgressIngestionDeadLetterReasons.StudentNotFound, ct);
            Phase4Metrics.IngestionDeadLettered.Add(1);
            return ProgressIngestionOutcome.Rejected;
        }

        var scope = ReadScope(envelope.CurriculumScope);
        var hasSubject = scope.SubjectId is not null;

        if (await _records.ExistsAsync(envelope.TenantId, envelope.SourceEventId, ct))
        {
            Phase4Metrics.IngestionDuplicate.Add(1);
            return ProgressIngestionOutcome.Duplicate;
        }

        var correlationId = string.IsNullOrWhiteSpace(envelope.CorrelationId)
            ? Guid.NewGuid().ToString("D")
            : envelope.CorrelationId;

        var record = new ProgressRecord
        {
            ProgressRecordId = Guid.NewGuid(),
            TenantId = envelope.TenantId,
            StudentId = envelope.StudentId,
            SourceEventId = envelope.SourceEventId,
            EventKind = envelope.EventKind,
            CurriculumScope = envelope.CurriculumScope is null ? "{}" : JsonSerializer.Serialize(envelope.CurriculumScope, JsonOptions),
            Payload = envelope.Payload is null ? "{}" : JsonSerializer.Serialize(envelope.Payload, JsonOptions),
            CorrelationId = correlationId,
            OccurredAt = envelope.OccurredAt.ToUniversalTime(),
            IngestedAt = DateTime.UtcNow,
        };

        IDbContextTransaction? tx = null;
        try
        {
            if (_db.Database.IsRelational())
            {
                tx = await _db.Database.BeginTransactionAsync(ct);
            }

            await _records.AddAsync(record, ct);
            // Flush the PR so the calculators (which query the table) include
            // it in their recompute, regardless of EF provider semantics.
            await _db.SaveChangesAsync(ct);

            MasteryRecomputeResult? masteryResult = null;
            if (hasSubject)
            {
                masteryResult = await _mastery.RecomputeAsync(
                    envelope.TenantId,
                    envelope.StudentId,
                    scope.SubjectId!.Value,
                    scope.TopicId,
                    scope.CurriculumType ?? "moe",
                    correlationId,
                    ct);
                Phase4Metrics.MasteryRecomputed.Add(1);
            }

            var streakResult = await _streak.RecomputeAsync(
                envelope.TenantId,
                envelope.StudentId,
                correlationId,
                ct);

            var badgeContext = new BadgeEvaluationContext(
                CurrentStreakLength: streakResult.NewLength,
                CurrentMasteryStates: await _masteryStates.ForStudentAsync(envelope.TenantId, envelope.StudentId, MasteryCalculator.Version, ct),
                PriorMasteryBand: masteryResult?.PriorBand ?? MasteryCalculator.BandIntroduced,
                NewMasteryBand: masteryResult?.NewBand ?? MasteryCalculator.BandIntroduced);

            var awarded = await _badges.EvaluateAsync(
                envelope.TenantId,
                envelope.StudentId,
                record.ProgressRecordId,
                correlationId,
                badgeContext,
                ct);

            if (masteryResult is { Changed: true })
            {
                Phase4Metrics.MasteryChanged.Add(1);
                await _downstream.EmitMasteryUpdatedAsync(
                    envelope.TenantId,
                    envelope.StudentId,
                    scope.CurriculumType ?? "moe",
                    scope.SubjectId!.Value,
                    scope.TopicId,
                    masteryResult.PriorScore,
                    masteryResult.NewScore,
                    masteryResult.PriorBand,
                    masteryResult.NewBand,
                    _mastery.CalculationVersion,
                    correlationId,
                    ct);
                Phase4Metrics.DownstreamEnqueued.Add(1);
            }

            if (streakResult.Changed)
            {
                Phase4Metrics.StreakChanged.Add(1);
                await _downstream.EmitStreakChangedAsync(
                    envelope.TenantId,
                    envelope.StudentId,
                    streakResult.PriorLength,
                    streakResult.NewLength,
                    streakResult.FamilyTimezone,
                    correlationId,
                    ct);
                Phase4Metrics.DownstreamEnqueued.Add(1);
            }

            if (awarded.Count > 0)
            {
                var badgeKeys = await _db.BadgeCriteria
                    .IgnoreQueryFilters()
                    .Where(c => awarded.Select(a => a.BadgeCriterionId).Contains(c.BadgeCriterionId))
                    .ToDictionaryAsync(c => c.BadgeCriterionId, c => c.BadgeKey, ct);

                foreach (var award in awarded)
                {
                    Phase4Metrics.BadgeAwarded.Add(1);
                    var key = badgeKeys.TryGetValue(award.BadgeCriterionId, out var bk) ? bk : string.Empty;
                    await _downstream.EmitBadgeAwardedAsync(
                        envelope.TenantId,
                        envelope.StudentId,
                        award,
                        key,
                        correlationId,
                        ct);
                    Phase4Metrics.DownstreamEnqueued.Add(1);
                }
            }

            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);

            Phase4Metrics.IngestionInserted.Add(1);
            return ProgressIngestionOutcome.Inserted;
        }
        catch (DbUpdateException)
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            var duplicate = await _records.ExistsAsync(envelope.TenantId, envelope.SourceEventId, ct);
            if (duplicate)
            {
                Phase4Metrics.IngestionDuplicate.Add(1);
                return ProgressIngestionOutcome.Duplicate;
            }
            throw;
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
            stopwatch.Stop();
            Phase4Metrics.IngestionLatencyMs.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    internal static CurriculumScopeDto ReadScope(object? raw)
    {
        if (raw is null) return CurriculumScopeDto.Empty;
        try
        {
            var json = raw is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(raw, JsonOptions);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return CurriculumScopeDto.Empty;

            string? curriculumType = null;
            if (root.TryGetProperty("curriculum_type", out var ct) && ct.ValueKind == JsonValueKind.String)
            {
                curriculumType = ct.GetString();
            }
            Guid? subjectId = null;
            if (root.TryGetProperty("subject_id", out var sid) && sid.ValueKind == JsonValueKind.String
                && Guid.TryParse(sid.GetString(), out var s)) subjectId = s;
            Guid? topicId = null;
            if (root.TryGetProperty("topic_id", out var tid) && tid.ValueKind == JsonValueKind.String
                && Guid.TryParse(tid.GetString(), out var t)) topicId = t;

            return new CurriculumScopeDto(curriculumType, subjectId, topicId);
        }
        catch
        {
            return CurriculumScopeDto.Empty;
        }
    }

    internal sealed record CurriculumScopeDto(string? CurriculumType, Guid? SubjectId, Guid? TopicId)
    {
        public static CurriculumScopeDto Empty { get; } = new(null, null, null);
    }
}

public static class ProgressIngestionWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ProgressIngestionWorker(this IServiceCollection services)
    {
        services.AddScoped<IProgressIngestionWorker, ProgressIngestionWorker>();
        return services;
    }
}
