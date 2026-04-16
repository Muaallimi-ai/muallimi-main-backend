using Muallimi.Domain.AiOperations;

namespace Muallimi.Application.AiOperations;

/// <summary>
/// T126 (Polish, FR-028) — Investigation-retention policy for AI operations.
/// FR-028 requires the platform to retain per-request decision records for a
/// defined investigation window sufficient for incident response, and to
/// allow correlation lookup by session, curriculum, prompt version, and
/// guardrail outcome. This policy is the single source of truth for the
/// retention window and for the correlation-lookup axes; the infrastructure
/// enforcer consumes it to purge records older than the window and to back
/// the operator query surface.
///
/// The policy intentionally lives in Application (pure) so it can be
/// unit-tested without a DbContext, while the EF-backed enforcer in
/// Infrastructure/AiOperations/ applies it against <see cref="AiRequestRecord"/>
/// and <see cref="RefusalEvent"/> rows.
/// </summary>
public sealed record RetentionPolicy(TimeSpan InvestigationWindow)
{
    /// <summary>
    /// Default investigation window for Phase 2: 90 days. Chosen so any
    /// quarterly incident review has at least one full window of decision
    /// records to trace through, while keeping the raw-record footprint
    /// bounded. Operators can override via DI.
    /// </summary>
    public static RetentionPolicy Default { get; } =
        new(InvestigationWindow: TimeSpan.FromDays(90));

    public DateTime ComputeCutoff(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("nowUtc must be UTC.", nameof(nowUtc));
        return nowUtc - InvestigationWindow;
    }

    public bool IsExpired(AiRequestRecord record, DateTime nowUtc)
        => record.OccurredAt < ComputeCutoff(nowUtc);

    public bool IsExpired(RefusalEvent refusal, DateTime nowUtc)
        => refusal.OccurredAt < ComputeCutoff(nowUtc);
}

/// <summary>
/// The correlation-lookup axes FR-028 requires. The AI operations query
/// surface (<c>GET /ai-operations/requests</c> and the incident-lookup
/// endpoint) must support every axis listed here so an investigator can
/// correlate a decision trail starting from any of them.
/// </summary>
public sealed record CorrelationLookupQuery(
    string? CorrelationId = null,
    Guid? SessionId = null,
    string? CurriculumType = null,
    string? Grade = null,
    string? Subject = null,
    string? PromptVersionId = null,
    string? GuardrailOutcome = null,
    DateTime? OccurredAfterUtc = null,
    DateTime? OccurredBeforeUtc = null)
{
    public bool IsEmpty =>
        CorrelationId is null
        && SessionId is null
        && CurriculumType is null
        && Grade is null
        && Subject is null
        && PromptVersionId is null
        && GuardrailOutcome is null
        && OccurredAfterUtc is null
        && OccurredBeforeUtc is null;

    public void Validate()
    {
        if (IsEmpty)
            throw new InvalidOperationException(
                "CorrelationLookupQuery must specify at least one axis (FR-028).");
        if (OccurredAfterUtc is { } a && OccurredBeforeUtc is { } b && a > b)
            throw new InvalidOperationException(
                "OccurredAfterUtc cannot be after OccurredBeforeUtc.");
    }
}

/// <summary>
/// Result of a retention pass: how many records were in scope for the
/// cutoff and how many were purged. Exposed so the enforcer can publish an
/// audit record of every retention run.
/// </summary>
public sealed record RetentionPassResult(
    DateTime CutoffUtc,
    int AiRequestRecordsPurged,
    int RefusalEventsPurged)
{
    public static RetentionPassResult Empty(DateTime cutoffUtc) =>
        new(cutoffUtc, 0, 0);
}

/// <summary>
/// Abstraction for the infrastructure enforcer so the Application layer can
/// schedule retention passes without depending on EF Core.
/// </summary>
public interface IInvestigationRetentionEnforcer
{
    Task<RetentionPassResult> EnforceAsync(DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<AiRequestRecord>> LookupAsync(
        CorrelationLookupQuery query,
        int take = 100,
        CancellationToken ct = default);
}
