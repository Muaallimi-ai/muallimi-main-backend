using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Parents.OperatorImpersonation;

namespace Muallimi.Api.Parents.ParentDashboard;

/// <summary>
/// T073 (US2) — GET /parent/dashboard/{child_id}.
///
/// Returns the per-child dashboard payload pinned by
/// <c>specs/006-engagement-progress-parent/contracts/parent-dashboard-contract.md</c>:
/// mastery summary, focus areas for the week, recent activity feed, latest
/// weekly report reference, at-risk flag, and a read-only plan snapshot.
///
/// Tenant isolation is enforced in two layers:
///   1. <see cref="IChildLinkRepository.GetActiveAsync"/> refuses the
///      request with 404 when the parent does not hold an active link to
///      the child — no 403, to avoid leaking cross-family existence.
///   2. <see cref="IParentDashboardService"/> filters every query on
///      <c>(tenant_id, student_id)</c>.
///
/// Short-TTL caching is served by <see cref="IDashboardQueryCache"/>; the
/// cache key is <c>(tenant, parent, child, dashboard)</c>.
///
/// Operator impersonation: when the caller sends the
/// <c>X-Operator-Actor-Id</c> header (optionally with
/// <c>X-Operator-Reason</c>), <see cref="IOperatorImpersonationAuditor"/>
/// writes a row for the <c>parent_dashboard</c> surface in the same
/// transaction as the response (see T074).
/// </summary>
public static class ParentDashboardEndpoint
{
    public const string Route = "/api/parent/dashboard/{childId:guid}";
    private const string CacheSlot = "dashboard";

    public static IEndpointRouteBuilder MapParentDashboard(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("ParentDashboard")
            .WithTags("ParentDashboard");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        Guid childId,
        HttpContext http,
        IParentDashboardService service,
        IChildLinkRepository links,
        IDashboardQueryCache cache,
        IOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var isImpersonation = ParentDashboardHeaders.TryGetOperatorContext(http, out var operatorActorId, out var reason);

        var link = await links.GetActiveAsync(tenantId, parentProfileId, childId, ct);
        if (link is null)
        {
            // 404 (not 403) so we never leak cross-family child existence.
            return Results.NotFound();
        }

        var cacheKey = new DashboardQueryCacheKey(tenantId, parentProfileId, childId, CacheSlot);
        var payload = await cache.GetAsync<ParentDashboardPayload>(cacheKey, ct);
        if (payload is null)
        {
            payload = await service.BuildDashboardAsync(tenantId, parentProfileId, childId, correlationId, ct);
            await cache.SetAsync(cacheKey, payload, ct: ct);
        }
        else
        {
            // Overlay the caller's correlation id so every render produces
            // its own tracing chain even when the data is cache-served.
            payload = payload with { CorrelationId = correlationId };
        }

        if (isImpersonation)
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                targetParentProfileId: parentProfileId,
                targetChildId: childId,
                surface: OperatorImpersonationSurfaces.ParentDashboard,
                reason: string.IsNullOrWhiteSpace(reason) ? "dashboard_view" : reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;

        return Results.Ok(new
        {
            child_id = payload.ChildId,
            curriculum_type = payload.CurriculumType,
            grade = payload.Grade,
            mastery_by_subject = payload.MasteryBySubject.Select(m => new
            {
                subject_id = m.SubjectId,
                subject_label_ar = m.SubjectLabelAr,
                subject_label_en = m.SubjectLabelEn,
                mastery_score = m.MasteryScore,
                mastery_band = m.MasteryBand,
                delta_since_last_week = m.DeltaSinceLastWeek,
            }),
            focus_areas_this_week = payload.FocusAreasThisWeek.Select(f => new
            {
                focus_area_id = f.FocusAreaId,
                subject_id = f.SubjectId,
                topic_id = f.TopicId,
                rationale_ar = f.RationaleAr,
                rationale_en = f.RationaleEn,
                suggested_next_step = new
                {
                    phase3_mode = f.SuggestedNextStep.Phase3Mode,
                    deep_link = f.SuggestedNextStep.DeepLink,
                },
            }),
            recent_activity = payload.RecentActivity.Select(r => new
            {
                occurred_at = r.OccurredAt,
                summary_ar = r.SummaryAr,
                summary_en = r.SummaryEn,
                curriculum_scope = r.CurriculumScope,
            }),
            latest_weekly_report = payload.LatestWeeklyReport is null ? null : new
            {
                weekly_report_id = payload.LatestWeeklyReport.WeeklyReportId,
                window_start = payload.LatestWeeklyReport.WindowStart,
                window_end = payload.LatestWeeklyReport.WindowEnd,
                summary_ar = payload.LatestWeeklyReport.SummaryAr,
                summary_en = payload.LatestWeeklyReport.SummaryEn,
                status = payload.LatestWeeklyReport.Status,
            },
            plan_view = new
            {
                plan_tier = payload.PlanView.PlanTier,
                entitlements = payload.PlanView.Entitlements,
                is_read_only = payload.PlanView.IsReadOnly,
            },
            at_risk_flag = payload.AtRiskFlag is null ? null : new
            {
                raised_at = payload.AtRiskFlag.RaisedAt,
                linked_intervention_prompt_id = payload.AtRiskFlag.LinkedInterventionPromptId,
                status = payload.AtRiskFlag.Status,
            },
            streak = new
            {
                current_length = payload.Streak.CurrentLength,
                longest_length = payload.Streak.LongestLength,
                last_qualifying_day = payload.Streak.LastQualifyingDay,
                family_timezone = payload.Streak.FamilyTimezone,
            },
            badges = payload.Badges.Select(b => new
            {
                badge_award_id = b.BadgeAwardId,
                badge_key = b.BadgeKey,
                badge_criterion_version = b.BadgeCriterionVersion,
                awarded_at = b.AwardedAt,
                display_name_ar = b.DisplayNameAr,
                display_name_en = b.DisplayNameEn,
            }),
            correlation_id = payload.CorrelationId,
        });
    }
}
