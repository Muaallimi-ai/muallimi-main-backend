using System;
using System.Collections.Generic;
using System.Text.Json;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// Shared projection used by <see cref="WeeklyReportViewEndpoint"/> and
/// <see cref="SharedReportViewEndpoint"/>. Pinned by the weekly report
/// contract — mastery_deltas, top_focus_areas, awarded_badges,
/// evidence_refs, summary_ar, summary_en, guardrail_decision_trail_id,
/// status, and the window boundaries.
/// </summary>
public sealed record WeeklyReportViewPayload(
    Guid WeeklyReportId,
    Guid TenantId,
    Guid ChildId,
    DateTime WindowStart,
    DateTime WindowEnd,
    DateTime GeneratedAt,
    string Status,
    IReadOnlyList<WeeklyMasteryDelta> MasteryDeltas,
    IReadOnlyList<WeeklyFocusAreaSnapshot> TopFocusAreas,
    IReadOnlyList<WeeklyBadgeAwardSnapshot> AwardedBadges,
    string SummaryAr,
    string SummaryEn,
    IReadOnlyList<WeeklyEvidenceRef> EvidenceRefs,
    Guid GuardrailDecisionTrailId,
    string CorrelationId);

public static class WeeklyReportProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static WeeklyReportViewPayload Project(WeeklyReport report, string correlationId)
    {
        return new WeeklyReportViewPayload(
            WeeklyReportId: report.WeeklyReportId,
            TenantId: report.TenantId,
            ChildId: report.StudentId,
            WindowStart: report.WindowStart,
            WindowEnd: report.WindowEnd,
            GeneratedAt: report.GeneratedAt,
            Status: report.Status,
            MasteryDeltas: DeserializeList<WeeklyMasteryDelta>(report.MasteryDeltas),
            TopFocusAreas: DeserializeList<WeeklyFocusAreaSnapshot>(report.TopFocusAreas),
            AwardedBadges: DeserializeList<WeeklyBadgeAwardSnapshot>(report.AwardedBadges),
            SummaryAr: report.SummaryAr,
            SummaryEn: report.SummaryEn,
            EvidenceRefs: DeserializeList<WeeklyEvidenceRef>(report.EvidenceRefs),
            GuardrailDecisionTrailId: report.GuardrailDecisionTrailId,
            CorrelationId: correlationId);
    }

    public static object ToWire(WeeklyReportViewPayload payload)
    {
        return new
        {
            weekly_report_id = payload.WeeklyReportId,
            tenant_id = payload.TenantId,
            child_id = payload.ChildId,
            window_start = payload.WindowStart.ToString("yyyy-MM-dd"),
            window_end = payload.WindowEnd.ToString("yyyy-MM-dd"),
            generated_at = payload.GeneratedAt,
            status = payload.Status,
            mastery_deltas = payload.MasteryDeltas,
            top_focus_areas = payload.TopFocusAreas,
            awarded_badges = payload.AwardedBadges,
            summary_ar = payload.SummaryAr,
            summary_en = payload.SummaryEn,
            evidence_refs = payload.EvidenceRefs,
            guardrail_decision_trail_id = payload.GuardrailDecisionTrailId,
            correlation_id = payload.CorrelationId,
        };
    }

    private static IReadOnlyList<T> DeserializeList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}" || json == "[]") return Array.Empty<T>();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? new List<T>();
        }
        catch
        {
            return Array.Empty<T>();
        }
    }
}
