using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.StudentExperience.Whiteboard;

/// <summary>
/// T117 (US8) — <see cref="WhiteboardSession"/> persistence.
///
/// Owns the <c>whiteboard_sessions</c> row lifecycle plus the <c>step_log</c>
/// jsonb column used for audit and for the <c>steps_played</c> count emitted
/// on the <c>whiteboard_session</c> end event.
///
/// Row lifecycle:
///   - <c>CreateAsync</c> starts the session with <c>plan_tier_snapshot</c>
///     captured at start; if the plan is revoked mid-run the endpoint layer
///     ends the session with <c>gate_revoked</c> rather than silently
///     continuing on the stale snapshot.
///   - <c>AppendStepAsync</c> records each step index plus the number of
///     draw operations so the end payload can surface the total without
///     re-counting the log.
///   - <c>EndAsync</c> writes <c>ended_at</c> + <c>end_reason</c> and leaves
///     the row in place for retention so investigations can correlate a
///     refusal with the session that triggered it.
///
/// EF's global query filter handles tenancy; the repository never re-checks
/// it itself and never writes to Phase 1 tables.
/// </summary>
public interface IWhiteboardSessionRepository
{
    Task<WhiteboardSession> CreateAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid subjectId,
        Guid topicId,
        string planTierSnapshot,
        string sessionMode,
        CancellationToken ct = default);

    Task<WhiteboardSession?> FindAsync(
        Guid whiteboardSessionId,
        CancellationToken ct = default);

    Task AppendStepAsync(
        WhiteboardSession session,
        int stepIndex,
        int drawOpsCount,
        CancellationToken ct = default);

    Task<WhiteboardSession> EndAsync(
        WhiteboardSession session,
        string endReason,
        CancellationToken ct = default);

    IReadOnlyList<WhiteboardStepLogEntry> ReadStepLog(WhiteboardSession session);
}

public sealed record WhiteboardStepLogEntry(
    [property: JsonPropertyName("step_index")] int StepIndex,
    [property: JsonPropertyName("draw_ops_count")] int DrawOpsCount,
    [property: JsonPropertyName("played_at")] DateTime PlayedAt);

public sealed class WhiteboardSessionRepository : IWhiteboardSessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MuallimiDbContext _db;

    public WhiteboardSessionRepository(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<WhiteboardSession> CreateAsync(
        Guid tenantId,
        Guid studentSessionId,
        Guid subjectId,
        Guid topicId,
        string planTierSnapshot,
        string sessionMode,
        CancellationToken ct = default)
    {
        var row = new WhiteboardSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentSessionId = studentSessionId,
            SubjectId = subjectId,
            TopicId = topicId,
            PlanTierSnapshot = planTierSnapshot,
            SessionMode = sessionMode,
            StepLog = "[]",
            StartedAt = DateTime.UtcNow,
        };
        _db.WhiteboardSessions.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    public Task<WhiteboardSession?> FindAsync(
        Guid whiteboardSessionId,
        CancellationToken ct = default) =>
        _db.WhiteboardSessions.FirstOrDefaultAsync(s => s.Id == whiteboardSessionId, ct);

    public async Task AppendStepAsync(
        WhiteboardSession session,
        int stepIndex,
        int drawOpsCount,
        CancellationToken ct = default)
    {
        var log = ReadStepLog(session).ToList();
        log.Add(new WhiteboardStepLogEntry(stepIndex, drawOpsCount, DateTime.UtcNow));
        session.StepLog = JsonSerializer.Serialize(log, JsonOptions);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<WhiteboardSession> EndAsync(
        WhiteboardSession session,
        string endReason,
        CancellationToken ct = default)
    {
        session.EndedAt = DateTime.UtcNow;
        session.EndReason = endReason;
        await _db.SaveChangesAsync(ct);
        return session;
    }

    public IReadOnlyList<WhiteboardStepLogEntry> ReadStepLog(WhiteboardSession session)
    {
        if (string.IsNullOrWhiteSpace(session.StepLog))
            return Array.Empty<WhiteboardStepLogEntry>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<WhiteboardStepLogEntry>>(
                session.StepLog, JsonOptions);
            return entries ?? new List<WhiteboardStepLogEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<WhiteboardStepLogEntry>();
        }
    }
}
