using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Tenancy;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.AiOperations;

/// <summary>
/// T102 (US6) — Operator-facing query surface over the Phase 2 AI operations
/// records. Every endpoint enforces the operator role via
/// <see cref="AiOperationsAuthorizationFilter"/>. The <c>question_text</c>
/// preview is redacted for every role except
/// <c>incident_investigation</c> (T097).
///
/// Routes:
///  - GET  /internal/ai-ops/requests           — filtered list
///  - GET  /internal/ai-ops/requests/{id}      — single record + refusals + prompt versions
///  - GET  /internal/ai-ops/metrics            — aggregate (rolled up per filter on the fly)
///  - GET  /internal/ai-ops/refusals           — refusal events with filters + redaction
///  - GET  /internal/ai-ops/readiness          — readiness-gate evaluation (FR-021)
/// </summary>
public static class AiOperationsEndpoints
{
    public const string RequestsRoute = "/internal/ai-ops/requests";
    public const string RequestByIdRoute = "/internal/ai-ops/requests/{recordId:guid}";
    public const string MetricsRoute = "/internal/ai-ops/metrics";
    public const string RefusalsRoute = "/internal/ai-ops/refusals";
    public const string ReadinessRoute = "/internal/ai-ops/readiness";

    public const string RedactedPreview = "[redacted]";

    public static void AddAiOperationsQueryServices(this IServiceCollection services)
    {
        services.AddSingleton<CostCalculator>();
        services.AddSingleton<MetricAggregator>();
        services.AddSingleton<ReadinessGateCheck>();
    }

    public static void MapAiOperationsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(RequestsRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            string? curriculumType,
            string? grade,
            string? subject,
            string? tutorLanguage,
            string? sessionMode,
            string? finalOutcome,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
        {
            if (!TryEnsureOperator(http, out var role, out var forbid)) return forbid!;

            var query = ApplyFilters(db.AiRequestRecords.AsNoTracking(),
                curriculumType, grade, subject, tutorLanguage, sessionMode, finalOutcome);

            var total = await query.CountAsync(ct);
            var pageSafe = Math.Max(1, page);
            var sizeSafe = Math.Clamp(pageSize, 1, 200);

            var items = await query
                .OrderByDescending(r => r.OccurredAt)
                .Skip((pageSafe - 1) * sizeSafe)
                .Take(sizeSafe)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                total,
                page = pageSafe,
                page_size = sizeSafe,
                role,
                filters = new { curriculumType, grade, subject, tutorLanguage, sessionMode, finalOutcome },
                items = items.Select(r => ProjectRecord(r, role)),
            });
        })
        .AddEndpointFilter<AiOperationsAuthorizationEndpointFilter>()
        .WithName("ListAiRequestRecords")
        .WithTags("AiOperations");

        routes.MapGet(RequestByIdRoute, async (
            Guid recordId,
            HttpContext http,
            MuallimiDbContext db,
            CancellationToken ct) =>
        {
            if (!TryEnsureOperator(http, out var role, out var forbid)) return forbid!;

            var record = await db.AiRequestRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.RecordId == recordId, ct);
            if (record is null)
                return Results.NotFound(new { error = $"Record '{recordId}' not found." });

            var refusals = await db.RefusalEvents.AsNoTracking()
                .Where(e => e.RecordId == recordId)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                role,
                record = ProjectRecord(record, role),
                refusals = refusals.Select(e => new
                {
                    event_id = e.EventId,
                    stage = e.Stage,
                    reason_code = e.ReasonCode,
                    localised_reason = e.LocalisedReason,
                    tutor_language = e.TutorLanguage,
                    occurred_at = e.OccurredAt,
                }),
            });
        })
        .AddEndpointFilter<AiOperationsAuthorizationEndpointFilter>()
        .WithName("GetAiRequestRecord")
        .WithTags("AiOperations");

        routes.MapGet(MetricsRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            MetricAggregator aggregator,
            string? curriculumType,
            string? grade,
            string? subject,
            string? tutorLanguage,
            string? sessionMode,
            DateTime? from,
            DateTime? to,
            CancellationToken ct = default) =>
        {
            if (!TryEnsureOperator(http, out var role, out var forbid)) return forbid!;

            var windowStart = from ?? DateTime.UtcNow.AddHours(-24);
            var windowEnd = to ?? DateTime.UtcNow;

            var query = db.AiRequestRecords.AsNoTracking()
                .Where(r => r.OccurredAt >= windowStart && r.OccurredAt <= windowEnd);
            query = ApplyFilters(query, curriculumType, grade, subject, tutorLanguage, sessionMode, finalOutcome: null);

            var rows = await query.ToListAsync(ct);
            var filter = new MetricSliceFilter(curriculumType, grade, subject, tutorLanguage, sessionMode);
            var aggregated = aggregator.Aggregate(rows, windowStart, windowEnd, filter);

            return Results.Ok(new
            {
                role,
                window_start = aggregated.WindowStart,
                window_end = aggregated.WindowEnd,
                filter = new
                {
                    curriculum_type = filter.CurriculumType,
                    grade = filter.Grade,
                    subject = filter.Subject,
                    tutor_language = filter.TutorLanguage,
                    session_mode = filter.SessionMode,
                },
                volume = aggregated.Volume,
                refusal_rate = aggregated.RefusalRate,
                cache_hit_rate = aggregated.CacheHitRate,
                grounded_answer_rate = aggregated.GroundedAnswerRate,
                per_branch = aggregated.PerBranch,
                prompt_version_distribution = aggregated.PromptVersionDistribution,
            });
        })
        .AddEndpointFilter<AiOperationsAuthorizationEndpointFilter>()
        .WithName("GetAiOperationsMetrics")
        .WithTags("AiOperations");

        routes.MapGet(RefusalsRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            string? curriculumType,
            string? grade,
            string? subject,
            string? tutorLanguage,
            string? sessionMode,
            string? stage,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
        {
            if (!TryEnsureOperator(http, out var role, out var forbid)) return forbid!;

            var recordsQuery = ApplyFilters(db.AiRequestRecords.AsNoTracking(),
                curriculumType, grade, subject, tutorLanguage, sessionMode, finalOutcome: null);
            // Inner join via subquery — EF Core will translate to an IN (...) list
            var recordLookup = recordsQuery.Select(r => new { r.RecordId, r.CurriculumType, r.Grade, r.Subject, r.TutorLanguage, r.SessionMode });
            var ids = await recordLookup.Select(x => x.RecordId).ToListAsync(ct);

            var eventsQuery = db.RefusalEvents.AsNoTracking()
                .Where(e => ids.Contains(e.RecordId));
            if (!string.IsNullOrWhiteSpace(stage))
                eventsQuery = eventsQuery.Where(e => e.Stage == stage);

            var total = await eventsQuery.CountAsync(ct);
            var pageSafe = Math.Max(1, page);
            var sizeSafe = Math.Clamp(pageSize, 1, 200);
            var events = await eventsQuery
                .OrderByDescending(e => e.OccurredAt)
                .Skip((pageSafe - 1) * sizeSafe)
                .Take(sizeSafe)
                .ToListAsync(ct);

            return Results.Ok(new
            {
                total,
                page = pageSafe,
                page_size = sizeSafe,
                role,
                filters = new { curriculumType, grade, subject, tutorLanguage, sessionMode, stage },
                items = events.Select(e => new
                {
                    event_id = e.EventId,
                    record_id = e.RecordId,
                    stage = e.Stage,
                    reason_code = e.ReasonCode,
                    localised_reason = e.LocalisedReason,
                    tutor_language = e.TutorLanguage,
                    occurred_at = e.OccurredAt,
                }),
            });
        })
        .AddEndpointFilter<AiOperationsAuthorizationEndpointFilter>()
        .WithName("ListRefusalEvents")
        .WithTags("AiOperations");

        routes.MapGet(ReadinessRoute, async (
            HttpContext http,
            MuallimiDbContext db,
            MetricAggregator aggregator,
            ReadinessGateCheck gate,
            string? curriculumType,
            string? grade,
            string? subject,
            string? tutorLanguage,
            string? sessionMode,
            DateTime? from,
            DateTime? to,
            CancellationToken ct = default) =>
        {
            if (!TryEnsureOperator(http, out var role, out var forbid)) return forbid!;

            var windowStart = from ?? DateTime.UtcNow.AddHours(-24);
            var windowEnd = to ?? DateTime.UtcNow;

            var query = db.AiRequestRecords.AsNoTracking()
                .Where(r => r.OccurredAt >= windowStart && r.OccurredAt <= windowEnd);
            query = ApplyFilters(query, curriculumType, grade, subject, tutorLanguage, sessionMode, finalOutcome: null);
            var rows = await query.ToListAsync(ct);

            var filter = new MetricSliceFilter(curriculumType, grade, subject, tutorLanguage, sessionMode);
            var aggregated = aggregator.Aggregate(rows, windowStart, windowEnd, filter);
            var latencyP95 = ComputeLatencyP95(rows);
            var result = gate.Evaluate(aggregated, latencyP95);

            return Results.Ok(new
            {
                role,
                window_start = aggregated.WindowStart,
                window_end = aggregated.WindowEnd,
                promotion_blocked = result.PromotionBlocked,
                cost_per_question = result.CostPerQuestion,
                latency_p95_ms = result.LatencyP95Ms,
                failures = result.Failures.Select(f => new
                {
                    target = f.Target,
                    observed = f.Observed,
                    threshold = f.Threshold,
                    message = f.Message,
                }),
                targets = new
                {
                    max_cost_per_question = gate.Targets.MaxCostPerQuestion,
                    min_cache_hit_rate = gate.Targets.MinCacheHitRate,
                    max_refusal_rate = gate.Targets.MaxRefusalRate,
                    max_latency_p95_ms = gate.Targets.MaxLatencyP95Ms,
                    min_grounded_answer_rate = gate.Targets.MinGroundedAnswerRate,
                },
            });
        })
        .AddEndpointFilter<AiOperationsAuthorizationEndpointFilter>()
        .WithName("GetAiOperationsReadiness")
        .WithTags("AiOperations");
    }

    public static object ProjectRecord(AiRequestRecord record, string? role)
    {
        var showPreview = string.Equals(role, AiOperationsAuthorizationFilter.IncidentInvestigationRole, StringComparison.Ordinal);
        return new
        {
            record_id = record.RecordId,
            correlation_id = record.CorrelationId,
            session_id = record.SessionId,
            tenant_id = record.TenantId,
            curriculum_type = record.CurriculumType,
            grade = record.Grade,
            subject = record.Subject,
            tutor_language = record.TutorLanguage,
            session_mode = record.SessionMode,
            stages = record.Stages,
            routing_decision = record.RoutingDecision,
            input_token_count = record.InputTokenCount,
            output_token_count = record.OutputTokenCount,
            latency_ms = record.LatencyMs,
            cache_match_score = record.CacheMatchScore,
            final_outcome = record.FinalOutcome,
            prompt_versions_used = record.PromptVersionsUsed,
            occurred_at = record.OccurredAt,
            question_text_preview = showPreview ? record.QuestionTextPreview : RedactedPreview,
        };
    }

    public static IQueryable<AiRequestRecord> ApplyFilters(
        IQueryable<AiRequestRecord> query,
        string? curriculumType,
        string? grade,
        string? subject,
        string? tutorLanguage,
        string? sessionMode,
        string? finalOutcome)
    {
        if (!string.IsNullOrWhiteSpace(curriculumType))
            query = query.Where(r => r.CurriculumType == curriculumType);
        if (!string.IsNullOrWhiteSpace(grade))
            query = query.Where(r => r.Grade == grade);
        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(r => r.Subject == subject);
        if (!string.IsNullOrWhiteSpace(tutorLanguage))
            query = query.Where(r => r.TutorLanguage == tutorLanguage);
        if (!string.IsNullOrWhiteSpace(sessionMode))
            query = query.Where(r => r.SessionMode == sessionMode);
        if (!string.IsNullOrWhiteSpace(finalOutcome))
            query = query.Where(r => r.FinalOutcome == finalOutcome);
        return query;
    }

    public static bool TryEnsureOperator(HttpContext http, out string? role, out IResult? forbidden)
    {
        role = http.Request.Headers["X-Actor-Role"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(role) ||
            (role != AiOperationsAuthorizationFilter.OperatorRole &&
             role != AiOperationsAuthorizationFilter.IncidentInvestigationRole))
        {
            forbidden = Results.Json(new { error = "Operator role required." }, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }
        http.Items[AiOperationsAuthorizationFilter.RoleItemKey] = role;
        forbidden = null;
        return true;
    }

    public static double ComputeLatencyP95(IReadOnlyList<AiRequestRecord> rows)
    {
        if (rows.Count == 0) return 0;
        var sorted = rows.Select(r => r.LatencyMs).OrderBy(x => x).ToArray();
        var index = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Length) index = sorted.Length - 1;
        return sorted[index];
    }
}

/// <summary>
/// Minimal-API endpoint filter equivalent of
/// <see cref="AiOperationsAuthorizationFilter"/>. Uses the same role header
/// contract so operator tooling can share the same authentication surface.
/// </summary>
public sealed class AiOperationsAuthorizationEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!AiOperationsEndpoints.TryEnsureOperator(context.HttpContext, out _, out var forbidden))
            return forbidden;
        return await next(context);
    }
}
