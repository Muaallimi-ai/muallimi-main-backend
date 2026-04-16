using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.AiOperations;

/// <summary>
/// T082 (US4, FR-016, SC-008) — Correlation-ID incident lookup. Given a
/// correlation_id, returns the full decision trail across AI request records,
/// refusal events, and the resolved prompt versions used at each stage. An
/// operator investigating a bad or suspicious tutor answer pastes the id and
/// gets everything required to reproduce and explain the outcome:
///  - the request record (stages, routing decision, token counts, latency)
///  - every refusal event emitted for that record
///  - the resolved prompt + version used at each stage, including the audit
///    trail entries that produced the currently active version
/// </summary>
public static class IncidentLookupEndpoint
{
    public const string Route = "/internal/ai-ops/incidents/{correlationId}";

    public static void MapIncidentLookupEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, async (
            string correlationId,
            MuallimiDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(correlationId))
                return Results.BadRequest(new { error = "correlation_id is required." });

            var records = await db.AiRequestRecords.AsNoTracking()
                .Where(r => r.CorrelationId == correlationId)
                .OrderBy(r => r.OccurredAt)
                .ToListAsync(ct);

            if (records.Count == 0)
                return Results.NotFound(new { error = $"No AI request records for correlation_id '{correlationId}'." });

            var recordIds = records.Select(r => r.RecordId).ToList();
            var refusals = await db.RefusalEvents.AsNoTracking()
                .Where(e => recordIds.Contains(e.RecordId))
                .ToListAsync(ct);

            var promptIdsUsed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                foreach (var id in ExtractPromptIds(r.PromptVersionsUsed))
                    promptIdsUsed.Add(id);
            }

            var prompts = await db.Prompts.AsNoTracking()
                .Where(p => promptIdsUsed.Contains(p.Name))
                .ToListAsync(ct);
            var promptByName = prompts.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

            var relatedAudit = await db.PromptAuditEntries.AsNoTracking()
                .Where(e => prompts.Select(p => p.PromptId).Contains(e.PromptId))
                .OrderBy(e => e.OccurredAt)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                correlation_id = correlationId,
                records = records.Select(r => new
                {
                    record_id = r.RecordId,
                    session_id = r.SessionId,
                    tenant_id = r.TenantId,
                    curriculum_type = r.CurriculumType,
                    grade = r.Grade,
                    subject = r.Subject,
                    tutor_language = r.TutorLanguage,
                    session_mode = r.SessionMode,
                    stages = r.Stages,
                    routing_decision = r.RoutingDecision,
                    input_token_count = r.InputTokenCount,
                    output_token_count = r.OutputTokenCount,
                    latency_ms = r.LatencyMs,
                    cache_match_score = r.CacheMatchScore,
                    final_outcome = r.FinalOutcome,
                    prompt_versions_used = r.PromptVersionsUsed,
                    occurred_at = r.OccurredAt,
                }),
                refusals = refusals.Select(e => new
                {
                    event_id = e.EventId,
                    record_id = e.RecordId,
                    stage = e.Stage,
                    reason_code = e.ReasonCode,
                    localised_reason = e.LocalisedReason,
                    tutor_language = e.TutorLanguage,
                    occurred_at = e.OccurredAt,
                }),
                prompts = prompts.Select(p => new
                {
                    prompt_id = p.PromptId,
                    name = p.Name,
                    active_version_id = p.ActiveVersionId,
                    status = p.Status,
                }),
                prompt_audit_trail = relatedAudit.Select(e => new
                {
                    entry_id = e.EntryId,
                    prompt_id = e.PromptId,
                    version_id = e.VersionId,
                    action = e.Action,
                    actor = e.Actor,
                    correlation_id = e.CorrelationId,
                    occurred_at = e.OccurredAt,
                    diff = e.Diff,
                }),
            });
        })
        .WithName("GetIncidentByCorrelationId")
        .WithTags("AiOperations");
    }

    public static IEnumerable<string> ExtractPromptIds(string promptVersionsUsedJson)
    {
        if (string.IsNullOrWhiteSpace(promptVersionsUsedJson)) yield break;

        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(promptVersionsUsedJson); }
        catch (System.Text.Json.JsonException) { yield break; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) yield break;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (property.Value.TryGetProperty("prompt_id", out var id) &&
                    id.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var name = id.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) yield return name!;
                }
            }
        }
    }
}
