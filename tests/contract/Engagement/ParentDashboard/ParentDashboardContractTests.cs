using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentDashboard;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Engagement.ParentDashboard;

/// <summary>
/// T063 (US2) — Contract tests for <c>phase4.parent.dashboard</c>.
///
/// Pins the payload shape + endpoint routes described by
/// <c>specs/006-engagement-progress-parent/contracts/parent-dashboard-contract.md</c>
/// so the frontend child selector and the Phase 5 operator tooling stay
/// stable when main-backend refactors the underlying storage.
/// </summary>
public class ParentDashboardContractTests
{
    [Fact]
    public void Dashboard_Payload_Carries_All_Top_Level_Fields()
    {
        var props = PropertyNamesOf<ParentDashboardPayload>();
        Assert.Contains("ChildId", props);
        Assert.Contains("CurriculumType", props);
        Assert.Contains("Grade", props);
        Assert.Contains("MasteryBySubject", props);
        Assert.Contains("FocusAreasThisWeek", props);
        Assert.Contains("RecentActivity", props);
        Assert.Contains("LatestWeeklyReport", props);
        Assert.Contains("PlanView", props);
        Assert.Contains("AtRiskFlag", props);
        Assert.Contains("CorrelationId", props);
    }

    [Fact]
    public void Mastery_Subject_Carries_Bilingual_Labels_And_Delta()
    {
        var props = PropertyNamesOf<ParentMasterySubject>();
        Assert.Contains("SubjectId", props);
        Assert.Contains("SubjectLabelAr", props);
        Assert.Contains("SubjectLabelEn", props);
        Assert.Contains("MasteryScore", props);
        Assert.Contains("MasteryBand", props);
        Assert.Contains("DeltaSinceLastWeek", props);
    }

    [Fact]
    public void Focus_Area_Carries_Bilingual_Rationale_And_Next_Step()
    {
        var props = PropertyNamesOf<ParentFocusArea>();
        Assert.Contains("FocusAreaId", props);
        Assert.Contains("SubjectId", props);
        Assert.Contains("TopicId", props);
        Assert.Contains("RationaleAr", props);
        Assert.Contains("RationaleEn", props);
        Assert.Contains("SuggestedNextStep", props);

        var stepProps = PropertyNamesOf<ParentFocusNextStep>();
        Assert.Contains("Phase3Mode", stepProps);
        Assert.Contains("DeepLink", stepProps);
    }

    [Fact]
    public void Recent_Activity_Carries_Bilingual_Summary_And_Scope()
    {
        var props = PropertyNamesOf<ParentRecentActivity>();
        Assert.Contains("OccurredAt", props);
        Assert.Contains("SummaryAr", props);
        Assert.Contains("SummaryEn", props);
        Assert.Contains("CurriculumScope", props);
    }

    [Fact]
    public void Weekly_Report_Reference_Carries_Window_And_Status()
    {
        var props = PropertyNamesOf<ParentLatestWeeklyReport>();
        Assert.Contains("WeeklyReportId", props);
        Assert.Contains("WindowStart", props);
        Assert.Contains("WindowEnd", props);
        Assert.Contains("SummaryAr", props);
        Assert.Contains("SummaryEn", props);
        Assert.Contains("Status", props);
    }

    [Fact]
    public void Plan_View_Is_Read_Only_And_Carries_Entitlements()
    {
        var props = PropertyNamesOf<ParentPlanView>();
        Assert.Contains("PlanTier", props);
        Assert.Contains("Entitlements", props);
        Assert.Contains("IsReadOnly", props);
    }

    [Fact]
    public void AtRisk_Flag_Carries_Raised_At_And_Intervention_Link()
    {
        var props = PropertyNamesOf<ParentAtRiskFlag>();
        Assert.Contains("RaisedAt", props);
        Assert.Contains("LinkedInterventionPromptId", props);
        Assert.Contains("Status", props);
    }

    [Fact]
    public void Child_List_Item_Exposes_Selector_Fields()
    {
        var props = PropertyNamesOf<ParentChildListItem>();
        Assert.Contains("ChildId", props);
        Assert.Contains("DisplayName", props);
        Assert.Contains("CurriculumType", props);
        Assert.Contains("Grade", props);
        Assert.Contains("PreferredLanguage", props);
    }

    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/parent/children", ParentChildrenEndpoint.Route);
        Assert.Equal("/api/parent/dashboard/{childId:guid}", ParentDashboardEndpoint.Route);
    }

    [Fact]
    public void Impersonation_Surface_Constants_Include_Parent_Dashboard()
    {
        Assert.Equal("parent_dashboard", OperatorImpersonationSurfaces.ParentDashboard);
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
