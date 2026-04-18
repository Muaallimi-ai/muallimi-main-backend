using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Api.Parents.OperatorImpersonation;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Engagement.WeeklyReport;

/// <summary>
/// T083 (US3) — Contract tests for <c>phase4.weekly.report</c>.
///
/// Pins the payload shape and endpoint routes described by
/// <c>specs/006-engagement-progress-parent/contracts/weekly-report-contract.md</c>
/// so the frontend weekly report viewer, the share-link flow, and the
/// Phase 5 downstream consumers stay stable when main-backend refactors
/// storage.
/// </summary>
public class WeeklyReportContractTests
{
    [Fact]
    public void View_Payload_Carries_All_Top_Level_Fields()
    {
        var props = PropertyNamesOf<WeeklyReportViewPayload>();
        Assert.Contains("WeeklyReportId", props);
        Assert.Contains("TenantId", props);
        Assert.Contains("ChildId", props);
        Assert.Contains("WindowStart", props);
        Assert.Contains("WindowEnd", props);
        Assert.Contains("GeneratedAt", props);
        Assert.Contains("Status", props);
        Assert.Contains("MasteryDeltas", props);
        Assert.Contains("TopFocusAreas", props);
        Assert.Contains("AwardedBadges", props);
        Assert.Contains("SummaryAr", props);
        Assert.Contains("SummaryEn", props);
        Assert.Contains("EvidenceRefs", props);
        Assert.Contains("GuardrailDecisionTrailId", props);
        Assert.Contains("CorrelationId", props);
    }

    [Fact]
    public void Mastery_Delta_Carries_Prior_And_New_Scores_With_Band()
    {
        var props = PropertyNamesOf<WeeklyMasteryDelta>();
        Assert.Contains("SubjectId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("PriorScore", props);
        Assert.Contains("NewScore", props);
        Assert.Contains("Band", props);
    }

    [Fact]
    public void Top_Focus_Area_Carries_Bilingual_Rationale_And_Next_Step()
    {
        var props = PropertyNamesOf<WeeklyFocusAreaSnapshot>();
        Assert.Contains("FocusAreaId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("RationaleAr", props);
        Assert.Contains("RationaleEn", props);
        Assert.Contains("Phase3Mode", props);
        Assert.Contains("DeepLink", props);
    }

    [Fact]
    public void Awarded_Badge_Carries_Version_And_Bilingual_Labels()
    {
        var props = PropertyNamesOf<WeeklyBadgeAwardSnapshot>();
        Assert.Contains("BadgeAwardId", props);
        Assert.Contains("BadgeKey", props);
        Assert.Contains("BadgeCriterionVersion", props);
        Assert.Contains("DisplayNameAr", props);
        Assert.Contains("DisplayNameEn", props);
    }

    [Fact]
    public void Evidence_Ref_Carries_Progress_And_Source_Event()
    {
        var props = PropertyNamesOf<WeeklyEvidenceRef>();
        Assert.Contains("ProgressRecordId", props);
        Assert.Contains("SourceEventId", props);
        Assert.Contains("CurriculumScope", props);
    }

    [Fact]
    public void Summary_Result_Carries_Bilingual_And_Trail_Id()
    {
        var props = PropertyNamesOf<WeeklyReportSummaryResult>();
        Assert.Contains("SummaryAr", props);
        Assert.Contains("SummaryEn", props);
        Assert.Contains("GuardrailDecisionTrailId", props);
        Assert.Contains("FinalStage", props);
    }

    [Fact]
    public void Shared_Report_Token_Claims_Are_Pinned()
    {
        var props = PropertyNamesOf<SharedReportTokenClaims>();
        Assert.Contains("TenantId", props);
        Assert.Contains("WeeklyReportId", props);
        Assert.Contains("ExpiresAt", props);
    }

    [Fact]
    public void Shared_Report_Token_Object_Carries_Raw_And_Expiry()
    {
        var props = PropertyNamesOf<SharedReportToken>();
        Assert.Contains("RawToken", props);
        Assert.Contains("ExpiresAt", props);
    }

    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/parent/reports/{reportId:guid}", WeeklyReportViewEndpoint.Route);
        Assert.Equal("/api/parent/reports/{reportId:guid}/share-link", WeeklyReportShareLinkEndpoint.Route);
        Assert.Equal("/api/parent/reports/{reportId:guid}/regenerate", WeeklyReportRegenerateEndpoint.Route);
        Assert.Equal("/api/reports/share/{shareToken}", SharedReportViewEndpoint.Route);
    }

    [Fact]
    public void Summary_Prompt_Key_Is_Weekly_Report_Summary()
    {
        Assert.Equal("weekly_report_summary", WeeklyReportSummaryGenerator.PromptKey);
        Assert.Equal("weekly_report_summary", GuardrailDecisionTrailArtefactKinds.WeeklyReportSummary);
    }

    [Fact]
    public void Impersonation_Surface_Constants_Include_Weekly_Report_Viewer()
    {
        Assert.Equal("weekly_report_viewer", OperatorImpersonationSurfaces.WeeklyReportViewer);
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
