using Muallimi.Application.Audit;
using Muallimi.Domain.Shared;

namespace Muallimi.Api.Coverage;

/// <summary>
/// T116 - GET /admin/content/coverage
///
/// Curriculum Admin / Subject Expert / Platform Operator facing coverage
/// dashboard. Supports optional curriculum type, grade, and subject filters.
/// Emits an audit event for every call so the dashboard access itself is
/// traceable.
/// </summary>
public static class CoverageEndpoints
{
    private static readonly HashSet<string> AllowedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "curriculum-admin",
        "subject-expert",
        "platform-operator"
    };

    public static IEndpointRouteBuilder MapCoverageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/content/coverage", async (
            HttpContext httpContext,
            CoverageAggregator aggregator,
            AuditEventEmitter audit,
            string? curriculumType,
            string? grade,
            string? subject) =>
        {
            var correlationId = httpContext.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString();
            var actor = httpContext.Items["ActorRole"]?.ToString() ?? "anonymous";
            var tenantId = httpContext.Items["TenantId"]?.ToString() ?? "local";

            if (actor != "anonymous" && !AllowedRoles.Contains(actor))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            CurriculumType? filterType = null;
            if (!string.IsNullOrWhiteSpace(curriculumType))
            {
                if (!Enum.TryParse<CurriculumType>(curriculumType, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"Unknown curriculum_type '{curriculumType}'." });
                filterType = parsed;
            }

            Grade? filterGrade = null;
            if (!string.IsNullOrWhiteSpace(grade))
            {
                if (!Enum.TryParse<Grade>(grade, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"Unknown grade '{grade}'." });
                filterGrade = parsed;
            }

            Subject? filterSubject = null;
            if (!string.IsNullOrWhiteSpace(subject))
            {
                if (!Enum.TryParse<Subject>(subject, ignoreCase: true, out var parsed))
                    return Results.BadRequest(new { error = $"Unknown subject '{subject}'." });
                filterSubject = parsed;
            }

            var filters = new CoverageFilters(filterType, filterGrade, filterSubject);
            var dashboard = await aggregator.BuildAsync(filters, DateTime.UtcNow, httpContext.RequestAborted);

            audit.Emit(new AuditEvent
            {
                EventCategory = "content",
                Action = "coverage-dashboard-viewed",
                TargetType = "CoverageDashboard",
                TargetId = $"lessons:{dashboard.TotalLessons}",
                ActorId = actor,
                TenantId = tenantId,
                Outcome = "succeeded",
                CorrelationId = correlationId,
                Reason = BuildFilterReason(filters)
            });

            return Results.Ok(new
            {
                filters = new
                {
                    curriculum_type = filters.CurriculumType?.ToString(),
                    grade = filters.Grade?.ToString(),
                    subject = filters.Subject?.ToString()
                },
                summary = new
                {
                    total_lessons = dashboard.TotalLessons,
                    sla_breached_count = dashboard.SlaBreachedCount,
                    by_state = dashboard.StateTotals
                        .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    by_asset_type = dashboard.AssetTypeTotals
                        .ToDictionary(
                            kv => kv.Key.ToString(),
                            kv => kv.Value.ToDictionary(s => s.Key.ToString(), s => s.Value))
                },
                lessons = dashboard.Lessons.Select(l => new
                {
                    lesson_id = l.LessonId,
                    curriculum_type = l.CurriculumType.ToString(),
                    grade = l.Grade.ToString(),
                    subject = l.Subject.ToString(),
                    path = l.Path,
                    assets = l.Assets.Select(a => new
                    {
                        asset_type = a.AssetType.ToString(),
                        state = a.State.ToString(),
                        queue_age_business_days = a.QueueAgeBusinessDays,
                        sla_threshold_business_days = a.SlaThresholdBusinessDays,
                        sla_breached = a.SlaBreached,
                        owner = a.Owner,
                        last_updated_at = a.LastUpdatedAt
                    })
                }),
                correlation_id = correlationId
            });
        })
        .WithName("GetContentCoverage")
        .WithTags("Content");

        return app;
    }

    private static string BuildFilterReason(CoverageFilters filters)
    {
        var parts = new List<string>();
        if (filters.CurriculumType is { } ct) parts.Add($"curriculum_type={ct}");
        if (filters.Grade is { } g) parts.Add($"grade={g}");
        if (filters.Subject is { } s) parts.Add($"subject={s}");
        return parts.Count == 0 ? "no-filters" : string.Join(";", parts);
    }
}
