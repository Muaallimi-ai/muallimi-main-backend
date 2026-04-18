using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Muallimi.Api.Engagement.AtRiskDetection;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.InterventionPrompts;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Domain.Engagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Engagement.AtRisk;

/// <summary>
/// T137 (US8) — Contract tests for <c>phase4.atrisk.intervention</c>.
///
/// Pins the endpoint routes, the <see cref="AtRiskFlag"/> +
/// <see cref="InterventionPrompt"/> schema, the intervention prompt key, and
/// the downstream event kinds described by
/// <c>specs/006-engagement-progress-parent/contracts/at-risk-intervention-contract.md</c>.
/// Frontend, Phase 5 consumers, and the operator-impersonation auditor all
/// assume these constants stay stable.
/// </summary>
public class AtRiskContractTests
{
    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/parent/at-risk/{childId:guid}", ParentAtRiskEndpoint.Route);
        Assert.Equal("/api/student/at-risk/self", StudentAtRiskSelfEndpoint.Route);
        Assert.Equal("/api/parent/at-risk/{flagId:guid}/acknowledge",
            ParentAtRiskAcknowledgeEndpoint.Route);
    }

    [Fact]
    public void AtRiskFlag_Carries_All_Contract_Fields()
    {
        var props = PropertyNamesOf<AtRiskFlag>();
        Assert.Contains("AtRiskFlagId", props);
        Assert.Contains("TenantId", props);
        Assert.Contains("StudentId", props);
        Assert.Contains("ThresholdVersion", props);
        Assert.Contains("TriggeringEvidence", props);
        Assert.Contains("RaisedAt", props);
        Assert.Contains("ClearedAt", props);
        Assert.Contains("LinkedInterventionPromptId", props);
        Assert.Contains("CorrelationId", props);
        Assert.Contains("AcknowledgedAt", props);
        Assert.Contains("AcknowledgedByParentProfileId", props);
    }

    [Fact]
    public void InterventionPrompt_Carries_All_Contract_Fields()
    {
        var props = PropertyNamesOf<InterventionPrompt>();
        Assert.Contains("InterventionPromptId", props);
        Assert.Contains("TenantId", props);
        Assert.Contains("StudentId", props);
        Assert.Contains("OriginatingFlagId", props);
        Assert.Contains("BodyAr", props);
        Assert.Contains("BodyEn", props);
        Assert.Contains("NextStep", props);
        Assert.Contains("GuardrailDecisionTrailId", props);
        Assert.Contains("CreatedAt", props);
        Assert.Contains("CorrelationId", props);
    }

    [Fact]
    public void Intervention_Prompt_Key_Matches_Tutor_Runtime_Reservation()
    {
        Assert.Equal("intervention_prompt", InterventionPromptGenerator.PromptKey);
        Assert.Equal("intervention_prompt", GuardrailDecisionTrailArtefactKinds.InterventionPrompt);
    }

    [Fact]
    public void Downstream_Event_Kinds_Include_Flagged_And_Cleared()
    {
        var names = System.Enum.GetNames(typeof(Phase4DownstreamEventKind)).ToHashSet();
        Assert.Contains("at_risk_flagged", names);
        Assert.Contains("at_risk_cleared", names);
    }

    [Fact]
    public void Impersonation_Surface_Includes_Intervention_Prompt()
    {
        Assert.Equal("intervention_prompt", OperatorImpersonationSurfaces.InterventionPrompt);
    }

    [Fact]
    public void Threshold_Catalogue_Exposes_Versioned_Default()
    {
        var catalogue = new AtRiskThresholdCatalogue();
        Assert.Equal(AtRiskThresholdCatalogue.CurrentVersion, catalogue.Current.Version);
        Assert.True(catalogue.Current.LowMasteryScoreCeiling > 0m);
        Assert.True(catalogue.Current.RecoveryMasteryScoreFloor
                    > catalogue.Current.LowMasteryScoreCeiling);

        var historical = catalogue.GetByVersion("v0.9.0");
        Assert.Equal("v0.9.0", historical.Version);
    }

    [Fact]
    public void Banned_Shaming_Tokens_Are_Detected()
    {
        Assert.NotEmpty(InterventionPromptGenerator.ContainsBannedTokens(
            "You are lazy and a failure."));
        Assert.NotEmpty(InterventionPromptGenerator.ContainsBannedTokens(
            "أنت كسلان وفاشل."));
        Assert.Empty(InterventionPromptGenerator.ContainsBannedTokens(
            "Let's keep moving forward with one small step this week."));
    }

    private static HashSet<string> PropertyNamesOf<T>()
        => typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
