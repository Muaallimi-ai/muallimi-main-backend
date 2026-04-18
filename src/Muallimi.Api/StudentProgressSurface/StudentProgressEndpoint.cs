using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Muallimi.Api.StudentProgressSurface;

/// <summary>
/// T052 (US1) — GET /student/progress/summary.
///
/// Returns the student's current mastery, streak, earned badges, and active
/// focus areas per the
/// <c>specs/006-engagement-progress-parent/contracts/student-progress-contract.md</c>
/// response shape.
///
/// Headers:
///   <c>X-Tenant-Id</c>             — resolves the tenant filter
///   <c>X-Student-Profile-Id</c>    — resolves the authenticated student;
///                                    missing header → 401
///   <c>X-Correlation-Id</c>        — echoed back in the response headers
///
/// Tenant isolation: the service filters every state table by
/// <c>(tenant_id, student_id)</c>; no row owned by any other student or
/// tenant can be returned.
/// </summary>
public static class StudentProgressEndpoint
{
    public const string Route = "/api/student/progress/summary";

    public static IEndpointRouteBuilder MapStudentProgressSummary(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Route, HandleAsync)
            .WithName("StudentProgressSummary")
            .WithTags("StudentProgressSurface");
        return routes;
    }

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        IStudentProgressService service,
        CancellationToken ct)
    {
        if (!StudentProgressHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!StudentProgressHeaders.TryGetStudentProfileId(http, out var studentId))
            return Results.Unauthorized();

        var correlationId = StudentProgressHeaders.ResolveCorrelationId(http);
        var summary = await service.BuildSummaryAsync(tenantId, studentId, ct);

        http.Response.Headers["X-Correlation-Id"] = correlationId.ToString();

        return Results.Ok(new
        {
            student_id = summary.StudentId,
            curriculum_type = summary.CurriculumType,
            mastery_by_subject = summary.MasteryBySubject.Select(s => new
            {
                subject_id = s.SubjectId,
                subject_label_ar = s.SubjectLabelAr,
                subject_label_en = s.SubjectLabelEn,
                mastery_score = s.MasteryScore,
                mastery_band = s.MasteryBand,
                topic_breakdown = s.TopicBreakdown.Select(t => new
                {
                    topic_id = t.TopicId,
                    topic_label_ar = t.TopicLabelAr,
                    topic_label_en = t.TopicLabelEn,
                    mastery_score = t.MasteryScore,
                    mastery_band = t.MasteryBand,
                }),
            }),
            streak = new
            {
                current_length = summary.Streak.CurrentLength,
                longest_length = summary.Streak.LongestLength,
                last_qualifying_day = summary.Streak.LastQualifyingDay,
                family_timezone = summary.Streak.FamilyTimezone,
            },
            badges = summary.Badges.Select(b => new
            {
                badge_award_id = b.BadgeAwardId,
                badge_key = b.BadgeKey,
                badge_criterion_version = b.BadgeCriterionVersion,
                awarded_at = b.AwardedAt,
                display_name_ar = b.DisplayNameAr,
                display_name_en = b.DisplayNameEn,
                celebration_shown = b.CelebrationShown,
            }),
            focus_areas = summary.FocusAreas.Select(f => new
            {
                focus_area_id = f.FocusAreaId,
                subject_id = f.SubjectId,
                chapter_id = f.ChapterId,
                topic_id = f.TopicId,
                rationale_ar = f.RationaleAr,
                rationale_en = f.RationaleEn,
                suggested_next_step = new
                {
                    phase3_mode = f.SuggestedNextStep.Phase3Mode,
                    deep_link = f.SuggestedNextStep.DeepLink,
                },
            }),
            correlation_id = correlationId,
        });
    }
}

internal static class StudentProgressHeaders
{
    public const string TenantHeaderName = "X-Tenant-Id";
    public const string StudentProfileHeaderName = "X-Student-Profile-Id";
    public const string CorrelationHeaderName = "X-Correlation-Id";

    public static bool TryGetTenantId(HttpContext http, out Guid tenantId)
        => Guid.TryParse(http.Request.Headers[TenantHeaderName].ToString(), out tenantId);

    public static bool TryGetStudentProfileId(HttpContext http, out Guid studentId)
        => Guid.TryParse(http.Request.Headers[StudentProfileHeaderName].ToString(), out studentId);

    public static Guid ResolveCorrelationId(HttpContext http)
    {
        var raw = http.Request.Headers[CorrelationHeaderName].ToString();
        return Guid.TryParse(raw, out var id) ? id : Guid.NewGuid();
    }
}
