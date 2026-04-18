using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.AiOperations.IncidentManagement;

/// <summary>
/// T080 + T087 — Incident lifecycle management per observability-contract.md.
/// Supports create, update (status transition open → investigating →
/// mitigated → resolved), timeline tracking, and runbook references. Every
/// create/status-change/resolution writes an AuditEntry via AuditTrailWriter.
/// </summary>
public interface IIncidentManagementService
{
    Task<IncidentRecord> CreateAsync(IncidentCreateCommand command, CancellationToken ct = default);
    Task<IncidentRecord?> UpdateAsync(Guid incidentId, IncidentUpdateCommand command, CancellationToken ct = default);
    Task<(IReadOnlyList<IncidentRecord> items, string? nextCursor)> ListAsync(IncidentQuery query, CancellationToken ct = default);
    Task<IncidentRecord?> GetAsync(Guid incidentId, CancellationToken ct = default);
}

public sealed class IncidentManagementService : IIncidentManagementService
{
    private static readonly string[] AllowedStatus = new[] { "open", "investigating", "mitigated", "resolved" };
    private static readonly string[] AllowedSeverity = new[] { "critical", "high", "medium", "low" };

    private readonly MuallimiDbContext _db;
    private readonly AuditTrailWriter _audit;

    public IncidentManagementService(MuallimiDbContext db, AuditTrailWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IncidentRecord> CreateAsync(IncidentCreateCommand command, CancellationToken ct = default)
    {
        if (!AllowedSeverity.Contains(command.Severity))
            throw new ArgumentException("severity must be critical|high|medium|low", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Title))
            throw new ArgumentException("title is required", nameof(command));

        var now = DateTime.UtcNow;
        var incident = new IncidentRecord
        {
            IncidentId = Guid.NewGuid(),
            Severity = command.Severity,
            Title = command.Title,
            Description = command.Description ?? string.Empty,
            AffectedServices = JsonSerializer.Serialize(command.AffectedServices ?? Array.Empty<string>()),
            AffectedTenants = command.AffectedTenants is null
                ? null
                : JsonSerializer.Serialize(command.AffectedTenants),
            RunbookReference = command.RunbookReference,
            Status = "open",
            OpenedBy = command.OpenedBy,
            OpenedAt = now,
            Timeline = JsonSerializer.Serialize(new[]
            {
                new TimelineEntry { Timestamp = now, Action = "incident_opened", Actor = command.OpenedBy.ToString("N") },
            }),
            CorrelationId = string.IsNullOrWhiteSpace(command.CorrelationId)
                ? Guid.NewGuid().ToString("N")
                : command.CorrelationId,
        };

        _db.IncidentRecords.Add(incident);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = Guid.Empty,
            ActorId = command.OpenedBy,
            ActorType = "operator",
            TargetId = incident.IncidentId,
            TargetType = "incident",
            ActionType = "incident.created",
            Payload = new { severity = incident.Severity, title = incident.Title, affected_services = command.AffectedServices },
            CorrelationId = incident.CorrelationId,
        }, ct);

        return incident;
    }

    public async Task<IncidentRecord?> UpdateAsync(Guid incidentId, IncidentUpdateCommand command, CancellationToken ct = default)
    {
        var incident = await _db.IncidentRecords.FirstOrDefaultAsync(x => x.IncidentId == incidentId, ct);
        if (incident is null) return null;

        var now = DateTime.UtcNow;
        var timeline = ParseTimeline(incident.Timeline);

        if (!string.IsNullOrWhiteSpace(command.Status))
        {
            if (!AllowedStatus.Contains(command.Status))
                throw new ArgumentException("status must be open|investigating|mitigated|resolved", nameof(command));
            if (!IsValidTransition(incident.Status, command.Status))
                throw new InvalidOperationException($"Invalid status transition {incident.Status} → {command.Status}.");

            incident.Status = command.Status;
            if (command.Status == "mitigated") incident.MitigatedAt = now;
            if (command.Status == "resolved") incident.ResolvedAt = now;

            timeline.Add(new TimelineEntry
            {
                Timestamp = now,
                Action = $"status_changed:{command.Status}",
                Actor = command.ActorId?.ToString("N"),
            });
        }

        if (command.RootCause is not null) incident.RootCause = command.RootCause;
        if (command.Resolution is not null) incident.Resolution = command.Resolution;
        if (command.RunbookReference is not null) incident.RunbookReference = command.RunbookReference;

        if (command.TimelineEntry is not null)
        {
            timeline.Add(new TimelineEntry
            {
                Timestamp = now,
                Action = command.TimelineEntry.Action,
                Actor = command.TimelineEntry.Actor ?? command.ActorId?.ToString("N"),
            });
        }

        incident.Timeline = JsonSerializer.Serialize(timeline);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = Guid.Empty,
            ActorId = command.ActorId ?? Guid.Empty,
            ActorType = "operator",
            TargetId = incident.IncidentId,
            TargetType = "incident",
            ActionType = command.Status == "resolved" ? "incident.resolved" : "incident.updated",
            Payload = new
            {
                status = incident.Status,
                root_cause = command.RootCause,
                resolution = command.Resolution,
            },
            CorrelationId = incident.CorrelationId,
        }, ct);

        return incident;
    }

    public async Task<(IReadOnlyList<IncidentRecord> items, string? nextCursor)> ListAsync(IncidentQuery query, CancellationToken ct = default)
    {
        var q = _db.IncidentRecords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Status)) q = q.Where(x => x.Status == query.Status);
        if (!string.IsNullOrWhiteSpace(query.Severity)) q = q.Where(x => x.Severity == query.Severity);

        if (!string.IsNullOrWhiteSpace(query.Cursor) &&
            DateTime.TryParse(query.Cursor, null, System.Globalization.DateTimeStyles.RoundtripKind, out var cursorTs))
        {
            q = q.Where(x => x.OpenedAt < cursorTs);
        }

        var limit = Math.Clamp(query.Limit, 1, 200);
        var rows = await q.OrderByDescending(x => x.OpenedAt).Take(limit + 1).ToListAsync(ct);
        string? next = null;
        if (rows.Count > limit)
        {
            next = rows[limit - 1].OpenedAt.ToString("O");
            rows = rows.Take(limit).ToList();
        }
        return (rows, next);
    }

    public Task<IncidentRecord?> GetAsync(Guid incidentId, CancellationToken ct = default)
        => _db.IncidentRecords.AsNoTracking().FirstOrDefaultAsync(x => x.IncidentId == incidentId, ct);

    internal static bool IsValidTransition(string from, string to)
    {
        return (from, to) switch
        {
            ("open", "investigating") => true,
            ("open", "mitigated") => true,
            ("open", "resolved") => true,
            ("investigating", "mitigated") => true,
            ("investigating", "resolved") => true,
            ("mitigated", "resolved") => true,
            ("mitigated", "investigating") => true,
            _ when from == to => true,
            _ => false,
        };
    }

    private static List<TimelineEntry> ParseTimeline(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<TimelineEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<TimelineEntry>>(json) ?? new List<TimelineEntry>();
        }
        catch
        {
            return new List<TimelineEntry>();
        }
    }
}

public sealed record IncidentCreateCommand(
    string Severity,
    string Title,
    string? Description,
    IReadOnlyList<string>? AffectedServices,
    IReadOnlyList<Guid>? AffectedTenants,
    string? CorrelationId,
    string? RunbookReference,
    Guid OpenedBy);

public sealed record IncidentUpdateCommand(
    string? Status,
    string? RootCause,
    string? Resolution,
    string? RunbookReference,
    TimelineEntryInput? TimelineEntry,
    Guid? ActorId);

public sealed record TimelineEntryInput(string Action, string? Actor);

public sealed record IncidentQuery(string? Status, string? Severity, string? Cursor, int Limit);

public sealed record TimelineEntry
{
    public DateTime Timestamp { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? Actor { get; init; }
}
