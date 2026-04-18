using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.AiOperations.IncidentManagement;

/// <summary>
/// T081 — Incident management API surface per observability-contract.md §155–209.
///
/// Routes (all operator-gated):
///  - GET /api/v1/operator/incidents
///  - POST /api/v1/operator/incidents
///  - PUT /api/v1/operator/incidents/{incidentId}
/// </summary>
public static class IncidentEndpoints
{
    public const string ListRoute = "/api/v1/operator/incidents";
    public const string CreateRoute = "/api/v1/operator/incidents";
    public const string UpdateRoute = "/api/v1/operator/incidents/{incidentId:guid}";

    public static IEndpointRouteBuilder MapIncidentManagementEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ListRoute, async (
            HttpContext http,
            IIncidentManagementService service,
            string? status,
            string? severity,
            string? cursor,
            int limit = 20,
            CancellationToken ct = default) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            var (items, next) = await service.ListAsync(new IncidentQuery(status, severity, cursor, limit), ct);
            return Results.Ok(new
            {
                incidents = items.Select(x => new
                {
                    incident_id = x.IncidentId,
                    severity = x.Severity,
                    title = x.Title,
                    status = x.Status,
                    affected_services = ParseJsonArray(x.AffectedServices),
                    opened_at = x.OpenedAt,
                    resolved_at = x.ResolvedAt,
                }),
                next_cursor = next,
            });
        })
        .WithName("ListIncidents")
        .WithTags("Observability");

        routes.MapPost(CreateRoute, async (
            HttpContext http,
            IIncidentManagementService service,
            IncidentCreateRequest body,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            var actorId = ParseActor(http);
            try
            {
                var incident = await service.CreateAsync(new IncidentCreateCommand(
                    Severity: body.Severity ?? "medium",
                    Title: body.Title ?? string.Empty,
                    Description: body.Description,
                    AffectedServices: body.AffectedServices,
                    AffectedTenants: null,
                    CorrelationId: body.CorrelationId ?? http.Request.Headers["X-Correlation-Id"].FirstOrDefault(),
                    RunbookReference: body.RunbookReference,
                    OpenedBy: actorId), ct);

                return Results.Ok(new
                {
                    incident_id = incident.IncidentId,
                    severity = incident.Severity,
                    title = incident.Title,
                    status = incident.Status,
                    opened_at = incident.OpenedAt,
                    correlation_id = incident.CorrelationId,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("CreateIncident")
        .WithTags("Observability");

        routes.MapPut(UpdateRoute, async (
            HttpContext http,
            IIncidentManagementService service,
            Guid incidentId,
            IncidentUpdateRequest body,
            CancellationToken ct) =>
        {
            if (!AiOperationsEndpoints.TryEnsureOperator(http, out _, out var forbid)) return forbid!;
            var actorId = ParseActor(http);
            try
            {
                var incident = await service.UpdateAsync(incidentId, new IncidentUpdateCommand(
                    Status: body.Status,
                    RootCause: body.RootCause,
                    Resolution: body.Resolution,
                    RunbookReference: body.RunbookReference,
                    TimelineEntry: body.TimelineEntry is null
                        ? null
                        : new TimelineEntryInput(body.TimelineEntry.Action ?? string.Empty, body.TimelineEntry.Actor),
                    ActorId: actorId), ct);

                if (incident is null) return Results.NotFound(new { error = $"Incident {incidentId} not found." });

                return Results.Ok(new
                {
                    incident_id = incident.IncidentId,
                    status = incident.Status,
                    mitigated_at = incident.MitigatedAt,
                    resolved_at = incident.ResolvedAt,
                    root_cause = incident.RootCause,
                    resolution = incident.Resolution,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .WithName("UpdateIncident")
        .WithTags("Observability");

        return routes;
    }

    private static Guid ParseActor(HttpContext http)
    {
        var header = http.Request.Headers["X-Actor-Id"].FirstOrDefault();
        return Guid.TryParse(header, out var g) ? g : Guid.Empty;
    }

    private static string[] ParseJsonArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

public sealed record IncidentCreateRequest(
    string? Severity,
    string? Title,
    string? Description,
    IReadOnlyList<string>? AffectedServices,
    string? CorrelationId,
    string? RunbookReference);

public sealed record IncidentUpdateRequest(
    string? Status,
    string? RootCause,
    string? Resolution,
    string? RunbookReference,
    IncidentTimelineEntryRequest? TimelineEntry);

public sealed record IncidentTimelineEntryRequest(string? Action, string? Actor);
