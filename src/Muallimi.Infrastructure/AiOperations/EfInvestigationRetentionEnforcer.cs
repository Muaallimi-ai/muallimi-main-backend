using Microsoft.EntityFrameworkCore;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.AiOperations;

/// <summary>
/// T126 (Polish, FR-028) — EF-backed enforcer for the
/// <see cref="RetentionPolicy"/>. Purges <see cref="AiRequestRecord"/> and
/// <see cref="RefusalEvent"/> rows older than the policy's investigation
/// window, and exposes a correlation-lookup surface spanning every axis
/// FR-028 requires (session, curriculum, prompt version, guardrail
/// outcome). The operator incident-lookup endpoint in Muallimi.Api consumes
/// <see cref="LookupAsync"/>; a scheduled worker invokes
/// <see cref="EnforceAsync"/> to roll the window forward.
/// </summary>
public sealed class EfInvestigationRetentionEnforcer : IInvestigationRetentionEnforcer
{
    private readonly MuallimiDbContext _db;
    private readonly RetentionPolicy _policy;

    public EfInvestigationRetentionEnforcer(
        MuallimiDbContext db,
        RetentionPolicy? policy = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _policy = policy ?? RetentionPolicy.Default;
    }

    public RetentionPolicy Policy => _policy;

    public async Task<RetentionPassResult> EnforceAsync(
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var cutoff = _policy.ComputeCutoff(nowUtc);

        var expiredRecords = await _db.AiRequestRecords
            .Where(r => r.OccurredAt < cutoff)
            .ToListAsync(ct);

        if (expiredRecords.Count == 0)
            return RetentionPassResult.Empty(cutoff);

        var expiredIds = expiredRecords.Select(r => r.RecordId).ToHashSet();
        var orphanedRefusals = await _db.RefusalEvents
            .Where(e => expiredIds.Contains(e.RecordId) || e.OccurredAt < cutoff)
            .ToListAsync(ct);

        _db.RefusalEvents.RemoveRange(orphanedRefusals);
        _db.AiRequestRecords.RemoveRange(expiredRecords);
        await _db.SaveChangesAsync(ct);

        return new RetentionPassResult(
            CutoffUtc: cutoff,
            AiRequestRecordsPurged: expiredRecords.Count,
            RefusalEventsPurged: orphanedRefusals.Count);
    }

    public async Task<IReadOnlyList<AiRequestRecord>> LookupAsync(
        CorrelationLookupQuery query,
        int take = 100,
        CancellationToken ct = default)
    {
        query.Validate();
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));

        IQueryable<AiRequestRecord> q = _db.AiRequestRecords.AsNoTracking();

        if (query.CorrelationId is { Length: > 0 } corr)
            q = q.Where(r => r.CorrelationId == corr);
        if (query.SessionId is { } sessionId)
            q = q.Where(r => r.SessionId == sessionId);
        if (query.CurriculumType is { Length: > 0 } curriculum)
            q = q.Where(r => r.CurriculumType == curriculum);
        if (query.Grade is { Length: > 0 } grade)
            q = q.Where(r => r.Grade == grade);
        if (query.Subject is { Length: > 0 } subject)
            q = q.Where(r => r.Subject == subject);
        if (query.OccurredAfterUtc is { } after)
            q = q.Where(r => r.OccurredAt >= after);
        if (query.OccurredBeforeUtc is { } before)
            q = q.Where(r => r.OccurredAt <= before);

        // PromptVersionId + GuardrailOutcome are stored as JSON on the record.
        // EF Core cannot translate a LINQ predicate against JSON text in a
        // provider-agnostic way here, so we materialise the pre-filtered set
        // and apply the final predicate in memory. The pre-filter (session,
        // curriculum, grade, subject, window) bounds the row count, keeping
        // the in-memory scan linear and predictable.
        var results = await q
            .OrderByDescending(r => r.OccurredAt)
            .Take(take * 4)
            .ToListAsync(ct);

        IEnumerable<AiRequestRecord> filtered = results;
        if (query.PromptVersionId is { Length: > 0 } promptVersion)
            filtered = filtered.Where(r => r.PromptVersionsUsed.Contains(promptVersion, StringComparison.Ordinal));
        if (query.GuardrailOutcome is { Length: > 0 } outcome)
            filtered = filtered.Where(r => r.Stages.Contains(outcome, StringComparison.Ordinal));

        return filtered.Take(take).ToList();
    }
}
