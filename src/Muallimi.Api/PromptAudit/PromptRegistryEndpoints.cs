using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.AiOperations;
using Muallimi.Application.AiOperations;
using Muallimi.Application.Audit;
using Muallimi.Domain.AiOperations;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Infrastructure.Persistence;
using Muallimi.Infrastructure.PromptAudit;

namespace Muallimi.Api.PromptAudit;

/// <summary>
/// T078 (US4) — Internal write endpoints for the prompt registry. Every
/// lifecycle action persists a <see cref="PromptAuditEntry"/>, emits an
/// <c>AuditEventEmitter</c> event with category=<c>prompt</c>, and publishes
/// a <c>prompt.promoted|archived|rolledback</c> envelope so the ai-service
/// registry client invalidates its cache. Promotion is rejected when the
/// target version has an open red-team regression
/// (<see cref="RedTeamEvaluationResult.PromotionBlockFlag"/>) or when the
/// prompt itself carries a <c>PromotionBlockFlag</c> (T075, FR-012).
/// Role enforcement (operator / curriculum lead) is applied by the
/// AiOperationsAuthorizationFilter wired in Phase 2 foundational.
/// </summary>
public static class PromptRegistryEndpoints
{
    public const string BaseRoute = "/internal/prompts";

    public static void MapPromptRegistryEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(BaseRoute, async (MuallimiDbContext db, string? status, string? owner, CancellationToken ct) =>
        {
            var query = db.Prompts.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(p => p.Status == status);

            var items = await query.OrderBy(p => p.Name).ToListAsync(ct);

            return Results.Ok(new
            {
                items = items.Select(p => new
                {
                    prompt_id = p.PromptId,
                    name = p.Name,
                    purpose = p.Purpose,
                    scope = p.Scope,
                    active_version_id = p.ActiveVersionId,
                    promotion_block_flag = p.PromotionBlockFlag,
                    status = p.Status,
                    created_at = p.CreatedAt,
                    updated_at = p.UpdatedAt,
                }),
            });
        })
        .WithName("ListPrompts")
        .WithTags("PromptRegistry");

        routes.MapGet(BaseRoute + "/{promptId:guid}", async (
            Guid promptId, MuallimiDbContext db, CancellationToken ct) =>
        {
            var prompt = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.PromptId == promptId, ct);
            if (prompt is null) return Results.NotFound(new { error = "Prompt not found." });

            PromptVersion? active = null;
            if (prompt.ActiveVersionId is Guid activeId)
                active = await db.PromptVersions.AsNoTracking().FirstOrDefaultAsync(v => v.VersionId == activeId, ct);

            return Results.Ok(new
            {
                prompt_id = prompt.PromptId,
                name = prompt.Name,
                purpose = prompt.Purpose,
                scope = prompt.Scope,
                active_version_id = prompt.ActiveVersionId,
                promotion_block_flag = prompt.PromotionBlockFlag,
                status = prompt.Status,
                declared_variables = active is null ? new string[] { } : active.ReadDeclaredVariables().ToArray(),
                created_at = prompt.CreatedAt,
                updated_at = prompt.UpdatedAt,
            });
        })
        .WithName("GetPrompt")
        .WithTags("PromptRegistry");

        routes.MapGet(BaseRoute + "/{promptId:guid}/versions", async (
            Guid promptId, MuallimiDbContext db, CancellationToken ct) =>
        {
            var versions = await db.PromptVersions.AsNoTracking()
                .Where(v => v.PromptId == promptId)
                .OrderByDescending(v => v.VersionNumber)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                items = versions.Select(v => new
                {
                    version_id = v.VersionId,
                    prompt_id = v.PromptId,
                    version_number = v.VersionNumber,
                    declared_variables = v.ReadDeclaredVariables(),
                    status = v.Status,
                    created_by = v.CreatedBy,
                    created_at = v.CreatedAt,
                }),
            });
        })
        .WithName("ListPromptVersions")
        .WithTags("PromptRegistry");

        routes.MapGet(BaseRoute + "/{promptId:guid}/versions/{versionId:guid}", async (
            Guid promptId, Guid versionId, MuallimiDbContext db, CancellationToken ct) =>
        {
            var version = await db.PromptVersions.AsNoTracking()
                .FirstOrDefaultAsync(v => v.PromptId == promptId && v.VersionId == versionId, ct);
            if (version is null) return Results.NotFound(new { error = "Prompt version not found." });

            return Results.Ok(new
            {
                version_id = version.VersionId,
                prompt_id = version.PromptId,
                version_number = version.VersionNumber,
                body = version.Body,
                declared_variables = version.ReadDeclaredVariables(),
                status = version.Status,
                created_by = version.CreatedBy,
                created_at = version.CreatedAt,
            });
        })
        .WithName("GetPromptVersion")
        .WithTags("PromptRegistry");

        routes.MapGet(BaseRoute + "/{promptId:guid}/audit", async (
            Guid promptId, MuallimiDbContext db, CancellationToken ct) =>
        {
            var entries = await db.PromptAuditEntries.AsNoTracking()
                .Where(e => e.PromptId == promptId)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                items = entries.Select(e => new
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
        .WithName("GetPromptAuditTrail")
        .WithTags("PromptRegistry");

        routes.MapPost(BaseRoute, async (
            HttpContext http,
            CreatePromptRequest request,
            MuallimiDbContext db,
            AuditEventEmitter audit,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "name is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor)) return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason)) return Results.BadRequest(new { error = "change_reason is required." });

            var existing = await db.Prompts.AnyAsync(p => p.Name == request.Name, ct);
            if (existing) return Results.Conflict(new { error = $"A prompt named '{request.Name}' already exists." });

            var (correlationId, tenantId) = ResolveContext(http);
            var prompt = Prompt.Create(request.Name, request.Purpose ?? string.Empty, request.Scope ?? "global");

            var audited = PromptAuditEntry.For(prompt.PromptId, null, PromptAuditActions.Created, request.ChangeActor,
                correlationId, diff: $"created prompt '{prompt.Name}'; reason={request.ChangeReason}");

            db.Prompts.Add(prompt);
            db.PromptAuditEntries.Add(audited);
            await db.SaveChangesAsync(ct);

            audit.Emit(new AuditEvent
            {
                EventCategory = "prompt",
                Action = PromptAuditActions.Created,
                TargetType = nameof(Prompt),
                TargetId = prompt.PromptId.ToString(),
                ActorId = request.ChangeActor,
                TenantId = tenantId,
                Outcome = "succeeded",
                CorrelationId = correlationId,
                Reason = request.ChangeReason,
            });

            return Results.Created($"{BaseRoute}/{prompt.PromptId}", new
            {
                prompt_id = prompt.PromptId,
                name = prompt.Name,
                status = prompt.Status,
                audit_entry_id = audited.EntryId,
            });
        })
        .WithName("CreatePrompt")
        .WithTags("PromptRegistry");

        routes.MapPost(BaseRoute + "/{promptId:guid}/versions", async (
            Guid promptId,
            HttpContext http,
            CreatePromptVersionRequest request,
            MuallimiDbContext db,
            AuditEventEmitter audit,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "body is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor)) return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason)) return Results.BadRequest(new { error = "change_reason is required." });

            var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.PromptId == promptId, ct);
            if (prompt is null) return Results.NotFound(new { error = "Prompt not found." });

            var nextNumber = await db.PromptVersions
                .Where(v => v.PromptId == promptId)
                .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;
            nextNumber += 1;

            var version = PromptVersion.Create(
                promptId, nextNumber, request.Body,
                request.DeclaredVariables ?? Array.Empty<string>(),
                request.ChangeActor);

            var (correlationId, tenantId) = ResolveContext(http);
            var audited = PromptAuditEntry.For(promptId, version.VersionId, PromptAuditActions.VersionCreated,
                request.ChangeActor, correlationId,
                diff: $"version_number={version.VersionNumber}; reason={request.ChangeReason}");

            db.PromptVersions.Add(version);
            db.PromptAuditEntries.Add(audited);
            await db.SaveChangesAsync(ct);

            audit.Emit(new AuditEvent
            {
                EventCategory = "prompt",
                Action = PromptAuditActions.VersionCreated,
                TargetType = nameof(PromptVersion),
                TargetId = version.VersionId.ToString(),
                ActorId = request.ChangeActor,
                TenantId = tenantId,
                Outcome = "succeeded",
                CorrelationId = correlationId,
                Reason = request.ChangeReason,
            });

            return Results.Created($"{BaseRoute}/{promptId}/versions/{version.VersionId}", new
            {
                version_id = version.VersionId,
                prompt_id = version.PromptId,
                version_number = version.VersionNumber,
                created_at = version.CreatedAt,
                audit_entry_id = audited.EntryId,
            });
        })
        .WithName("CreatePromptVersion")
        .WithTags("PromptRegistry");

        routes.MapPost(BaseRoute + "/{promptId:guid}/promote", async (
            Guid promptId,
            HttpContext http,
            PromoteRequest request,
            MuallimiDbContext db,
            AuditEventEmitter audit,
            IPromptRegistryEventPublisher publisher,
            MetricAggregator metricAggregator,
            ReadinessGateCheck readinessGate,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor)) return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason)) return Results.BadRequest(new { error = "change_reason is required." });

            var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.PromptId == promptId, ct);
            if (prompt is null) return Results.NotFound(new { error = "Prompt not found." });

            if (prompt.Status == PromptStatus.Archived)
                return Results.Conflict(new { error = "Archived prompts cannot be promoted." });

            if (prompt.PromotionBlockFlag)
                return Results.Conflict(new
                {
                    error = "Prompt is gated by an open red-team regression (T116, FR-023). Clear the flag by re-running the readiness scenario set against a fixed configuration.",
                    prompt_id = prompt.PromptId,
                    prompt_name = prompt.Name,
                });

            var target = await db.PromptVersions.FirstOrDefaultAsync(v => v.VersionId == request.VersionId && v.PromptId == promptId, ct);
            if (target is null) return Results.NotFound(new { error = "Target version not found for this prompt." });
            if (target.Status == PromptVersionStatus.Archived)
                return Results.Conflict(new { error = "Cannot promote an archived version." });

            var openRegression = await db.RedTeamEvaluationResults
                .AsNoTracking()
                .Where(r => r.PromotionBlockFlag)
                .OrderByDescending(r => r.RunAt)
                .FirstOrDefaultAsync(ct);
            if (openRegression is not null)
                return Results.Conflict(new
                {
                    error = "Promotion is blocked by an open red-team regression (T075/T116, FR-012, FR-023).",
                    result_id = openRegression.ResultId,
                    set_version = openRegression.SetVersion,
                    run_at = openRegression.RunAt,
                    fail_count = openRegression.FailCount,
                    regressions = openRegression.Regressions,
                });

            // T106 (US6, FR-021) — readiness gate. Block promotion when the
            // current 24h operational metrics miss any of the cost / cache-hit
            // / refusal / latency / grounded-answer targets. The gate is a
            // no-op when there's no traffic yet (Volume == 0).
            if (!request.IgnoreReadinessGate)
            {
                var windowStart = DateTime.UtcNow.AddHours(-24);
                var windowEnd = DateTime.UtcNow;
                var rows = await db.AiRequestRecords.AsNoTracking()
                    .Where(r => r.OccurredAt >= windowStart && r.OccurredAt <= windowEnd)
                    .ToListAsync(ct);
                var aggregated = metricAggregator.Aggregate(rows, windowStart, windowEnd);
                var latencyP95 = AiOperationsEndpoints.ComputeLatencyP95(rows);
                var gateResult = readinessGate.Evaluate(aggregated, latencyP95);
                if (gateResult.PromotionBlocked)
                {
                    return Results.Conflict(new
                    {
                        error = "Promotion blocked by the AI operations readiness gate (T106, FR-021).",
                        failures = gateResult.Failures.Select(f => new
                        {
                            target = f.Target,
                            observed = f.Observed,
                            threshold = f.Threshold,
                            message = f.Message,
                        }),
                    });
                }
            }

            var previousVersionId = prompt.ActiveVersionId;
            if (previousVersionId is Guid prevId && prevId != target.VersionId)
            {
                var previous = await db.PromptVersions.FirstOrDefaultAsync(v => v.VersionId == prevId, ct);
                previous?.MarkSuperseded();
            }

            prompt.SetActiveVersion(target.VersionId);
            target.MarkActive();

            var (correlationId, tenantId) = ResolveContext(http);
            var audited = PromptAuditEntry.For(prompt.PromptId, target.VersionId, PromptAuditActions.Promoted,
                request.ChangeActor, correlationId,
                diff: $"from={previousVersionId?.ToString() ?? "none"}; to={target.VersionId}; reason={request.ChangeReason}");

            db.PromptAuditEntries.Add(audited);
            await db.SaveChangesAsync(ct);

            audit.Emit(new AuditEvent
            {
                EventCategory = "prompt",
                Action = PromptAuditActions.Promoted,
                TargetType = nameof(PromptVersion),
                TargetId = target.VersionId.ToString(),
                ActorId = request.ChangeActor,
                TenantId = tenantId,
                Outcome = "succeeded",
                CorrelationId = correlationId,
                Reason = request.ChangeReason,
            });

            await publisher.PublishAsync(new PromptRegistryEvent(
                EventId: Guid.NewGuid().ToString("N"),
                EventType: PromptRegistryEventTypes.Promoted,
                PromptId: prompt.PromptId,
                VersionId: target.VersionId,
                ActorId: request.ChangeActor,
                CorrelationId: correlationId,
                OccurredAt: DateTime.UtcNow), ct);

            return Results.Ok(new
            {
                prompt_id = prompt.PromptId,
                active_version_id = prompt.ActiveVersionId,
                promoted_from = previousVersionId,
                audit_entry_id = audited.EntryId,
            });
        })
        .WithName("PromotePrompt")
        .WithTags("PromptRegistry");

        routes.MapPost(BaseRoute + "/{promptId:guid}/archive", async (
            Guid promptId,
            HttpContext http,
            ArchiveRequest request,
            MuallimiDbContext db,
            AuditEventEmitter audit,
            IPromptRegistryEventPublisher publisher,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor)) return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason)) return Results.BadRequest(new { error = "change_reason is required." });

            var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.PromptId == promptId, ct);
            if (prompt is null) return Results.NotFound(new { error = "Prompt not found." });

            prompt.Archive();
            var versions = await db.PromptVersions.Where(v => v.PromptId == promptId).ToListAsync(ct);
            foreach (var v in versions) v.MarkArchived();

            var (correlationId, tenantId) = ResolveContext(http);
            var audited = PromptAuditEntry.For(prompt.PromptId, prompt.ActiveVersionId, PromptAuditActions.Archived,
                request.ChangeActor, correlationId, diff: $"reason={request.ChangeReason}");

            db.PromptAuditEntries.Add(audited);
            await db.SaveChangesAsync(ct);

            audit.Emit(new AuditEvent
            {
                EventCategory = "prompt",
                Action = PromptAuditActions.Archived,
                TargetType = nameof(Prompt),
                TargetId = prompt.PromptId.ToString(),
                ActorId = request.ChangeActor,
                TenantId = tenantId,
                Outcome = "succeeded",
                CorrelationId = correlationId,
                Reason = request.ChangeReason,
            });

            await publisher.PublishAsync(new PromptRegistryEvent(
                EventId: Guid.NewGuid().ToString("N"),
                EventType: PromptRegistryEventTypes.Archived,
                PromptId: prompt.PromptId,
                VersionId: prompt.ActiveVersionId,
                ActorId: request.ChangeActor,
                CorrelationId: correlationId,
                OccurredAt: DateTime.UtcNow), ct);

            return Results.Ok(new
            {
                prompt_id = prompt.PromptId,
                status = prompt.Status,
                audit_entry_id = audited.EntryId,
            });
        })
        .WithName("ArchivePrompt")
        .WithTags("PromptRegistry");

        routes.MapPost(BaseRoute + "/{promptId:guid}/rollback", async (
            Guid promptId,
            HttpContext http,
            RollbackRequest request,
            MuallimiDbContext db,
            AuditEventEmitter audit,
            IPromptRegistryEventPublisher publisher,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor)) return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason)) return Results.BadRequest(new { error = "change_reason is required." });

            var prompt = await db.Prompts.FirstOrDefaultAsync(p => p.PromptId == promptId, ct);
            if (prompt is null) return Results.NotFound(new { error = "Prompt not found." });
            if (prompt.Status == PromptStatus.Archived)
                return Results.Conflict(new { error = "Archived prompts cannot be rolled back." });

            var current = prompt.ActiveVersionId;
            if (current is null) return Results.Conflict(new { error = "No active version — nothing to roll back to." });

            var predecessor = await db.PromptVersions
                .Where(v => v.PromptId == promptId && v.VersionId != current && v.Status != PromptVersionStatus.Archived)
                .OrderByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync(ct);
            if (predecessor is null)
                return Results.Conflict(new { error = "No prior active-eligible version to roll back to." });

            var currentVersion = await db.PromptVersions.FirstOrDefaultAsync(v => v.VersionId == current, ct);
            currentVersion?.MarkSuperseded();

            prompt.SetActiveVersion(predecessor.VersionId);
            predecessor.MarkActive();

            var (correlationId, tenantId) = ResolveContext(http);
            var audited = PromptAuditEntry.For(prompt.PromptId, predecessor.VersionId, PromptAuditActions.RolledBack,
                request.ChangeActor, correlationId,
                diff: $"from={current}; to={predecessor.VersionId}; reason={request.ChangeReason}");

            db.PromptAuditEntries.Add(audited);
            await db.SaveChangesAsync(ct);

            audit.Emit(new AuditEvent
            {
                EventCategory = "prompt",
                Action = PromptAuditActions.RolledBack,
                TargetType = nameof(PromptVersion),
                TargetId = predecessor.VersionId.ToString(),
                ActorId = request.ChangeActor,
                TenantId = tenantId,
                Outcome = "succeeded",
                CorrelationId = correlationId,
                Reason = request.ChangeReason,
            });

            await publisher.PublishAsync(new PromptRegistryEvent(
                EventId: Guid.NewGuid().ToString("N"),
                EventType: PromptRegistryEventTypes.RolledBack,
                PromptId: prompt.PromptId,
                VersionId: predecessor.VersionId,
                ActorId: request.ChangeActor,
                CorrelationId: correlationId,
                OccurredAt: DateTime.UtcNow), ct);

            return Results.Ok(new
            {
                prompt_id = prompt.PromptId,
                active_version_id = prompt.ActiveVersionId,
                rolled_back_from = current,
                audit_entry_id = audited.EntryId,
            });
        })
        .WithName("RollbackPrompt")
        .WithTags("PromptRegistry");
    }

    public static void AddPromptRegistryServices(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryPromptRegistryEventPublisher>();
        services.AddSingleton<IPromptRegistryEventPublisher>(
            sp => sp.GetRequiredService<InMemoryPromptRegistryEventPublisher>());
    }

    private static (string CorrelationId, string TenantId) ResolveContext(HttpContext http)
    {
        var correlationId = http.Items["CorrelationId"]?.ToString()
            ?? http.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");
        var tenantId = http.Items["TenantId"]?.ToString()
            ?? http.Request.Headers["X-Tenant-Id"].FirstOrDefault()
            ?? "local";
        return (correlationId, tenantId);
    }
}

public record CreatePromptRequest(string Name, string? Purpose, string? Scope, string ChangeActor, string ChangeReason);

public record CreatePromptVersionRequest(string Body, IReadOnlyList<string>? DeclaredVariables, string ChangeActor, string ChangeReason);

public record PromoteRequest(Guid VersionId, string ChangeActor, string ChangeReason, bool IgnoreReadinessGate = false);

public record ArchiveRequest(string ChangeActor, string ChangeReason);

public record RollbackRequest(string ChangeActor, string ChangeReason);
