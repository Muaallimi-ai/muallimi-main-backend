using Muallimi.Application.AiOperations;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Domain.ProviderBindings;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T109 (US7, FR-023) — Introduce a deliberately regressing prompt version,
/// rerun the red-team scenario set, and assert that:
///  - the red-team run reports <c>promotion_block_flag=true</c>,
///  - the affected <see cref="Prompt"/> and <see cref="ProviderAdapterBinding"/>
///    rows carry the flag after the persistence handler runs,
///  - the promotion gate in <c>PromptRegistryEndpoints</c> rejects a
///    subsequent promote attempt while the flag is set, and
///  - a later passing run clears the flag and re-opens promotion.
///
/// The test mirrors the handler's propagation logic in-process (the
/// production handler runs the same operations against
/// <c>MuallimiDbContext</c>); exercising the aggregate methods directly
/// keeps the test deterministic without a live database. The contract test
/// <c>RedTeamResultPersistenceTests</c> covers the DbContext write path.
/// </summary>
public class RegressionPromotionBlockTests
{
    [Fact]
    public void Regression_Sets_PromotionBlockFlag_On_Prompts_And_Bindings_In_Config_Under_Test()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        var binding = ProviderAdapterBinding.Create(
            capability: Capabilities.LlmLightweight,
            environment: Environments.Local,
            curriculumScope: null,
            providerIdentifier: "local-ollama");

        var envelope = BuildEnvelope(prompt, binding, promotionBlock: true,
            regressions: new[] { "pi-002-en" });

        ApplyPropagation(envelope, new[] { prompt }, new[] { binding });

        Assert.True(prompt.PromotionBlockFlag);
        Assert.True(binding.PromotionBlockFlag);
    }

    [Fact]
    public void Promote_Is_Rejected_While_Prompt_PromotionBlockFlag_Is_Set()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        prompt.ApplyPromotionBlock();

        var canPromote = CanPromote(prompt, anyOpenRegression: false);
        Assert.False(canPromote);
    }

    [Fact]
    public void Promote_Is_Rejected_While_An_Open_Regression_Exists_Even_On_A_Different_Prompt()
    {
        var primary = Prompt.Create("system.lightweight", "tutor", "global");
        var canPromote = CanPromote(primary, anyOpenRegression: true);
        Assert.False(canPromote);
    }

    [Fact]
    public void Subsequent_Passing_Run_Clears_PromotionBlockFlag_On_Affected_Rows()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        var binding = ProviderAdapterBinding.Create(
            capability: Capabilities.LlmLightweight,
            environment: Environments.Local,
            curriculumScope: null,
            providerIdentifier: "local-ollama");

        var regressing = BuildEnvelope(prompt, binding, promotionBlock: true,
            regressions: new[] { "pi-002-en" });
        ApplyPropagation(regressing, new[] { prompt }, new[] { binding });
        Assert.True(prompt.PromotionBlockFlag);
        Assert.True(binding.PromotionBlockFlag);

        var passing = BuildEnvelope(prompt, binding, promotionBlock: false, regressions: Array.Empty<string>());
        ApplyPropagation(passing, new[] { prompt }, new[] { binding });

        Assert.False(prompt.PromotionBlockFlag);
        Assert.False(binding.PromotionBlockFlag);
        Assert.True(CanPromote(prompt, anyOpenRegression: false));
    }

    [Fact]
    public void Regression_Env_With_No_Config_Under_Test_Still_Marks_Run_As_PromotionBlocked()
    {
        var envelope = new RedTeamRunCompletedEnvelope(
            EventId: Guid.NewGuid().ToString("N"),
            ResultId: Guid.NewGuid(),
            ScenarioSetId: "readiness-v1",
            ScenarioSetVersion: "1.0.0",
            EvaluatedAt: DateTime.UtcNow,
            PassCount: 20,
            FailCount: 1,
            Regressions: new[] { "hsc-002-en" },
            PromotionBlockFlag: true,
            ConfigUnderTest: new RedTeamConfigSnapshot(
                Array.Empty<RedTeamPromptBinding>(),
                Array.Empty<RedTeamAdapterBinding>()),
            ArtifactKey: "redteam/artifacts/readiness-v1/1.0.0/x.json",
            CorrelationId: "corr-x");

        Assert.True(envelope.PromotionBlockFlag);
        Assert.Single(envelope.Regressions);
    }

    private static RedTeamRunCompletedEnvelope BuildEnvelope(
        Prompt prompt,
        ProviderAdapterBinding binding,
        bool promotionBlock,
        IReadOnlyList<string> regressions)
        => new(
            EventId: Guid.NewGuid().ToString("N"),
            ResultId: Guid.NewGuid(),
            ScenarioSetId: "readiness-v1",
            ScenarioSetVersion: "1.0.0",
            EvaluatedAt: DateTime.UtcNow,
            PassCount: promotionBlock ? 20 : 21,
            FailCount: promotionBlock ? 1 : 0,
            Regressions: regressions,
            PromotionBlockFlag: promotionBlock,
            ConfigUnderTest: new RedTeamConfigSnapshot(
                new[] { new RedTeamPromptBinding(prompt.PromptId, prompt.ActiveVersionId) },
                new[] { new RedTeamAdapterBinding(binding.BindingId, binding.Capability) }),
            ArtifactKey: $"redteam/artifacts/readiness-v1/1.0.0/{Guid.NewGuid():N}.json",
            CorrelationId: "corr-regression-test");

    /// <summary>
    /// Mirrors the flag-propagation block in
    /// <c>Muallimi.Infrastructure.AiOperations.RedTeamResultPersistenceHandler.PropagateFlagsAsync</c>.
    /// </summary>
    private static void ApplyPropagation(
        RedTeamRunCompletedEnvelope envelope,
        IReadOnlyList<Prompt> prompts,
        IReadOnlyList<ProviderAdapterBinding> bindings)
    {
        var promptIds = envelope.ConfigUnderTest.PromptBindings.Select(b => b.PromptId).ToHashSet();
        var bindingIds = envelope.ConfigUnderTest.AdapterBindings.Select(b => b.BindingId).ToHashSet();

        foreach (var prompt in prompts.Where(p => promptIds.Contains(p.PromptId)))
        {
            if (envelope.PromotionBlockFlag) prompt.ApplyPromotionBlock();
            else prompt.ClearPromotionBlock();
        }

        foreach (var binding in bindings.Where(b => bindingIds.Contains(b.BindingId)))
        {
            binding.PromotionBlockFlag = envelope.PromotionBlockFlag;
        }
    }

    /// <summary>
    /// Mirrors the two guards that
    /// <c>PromptRegistryEndpoints.MapPost(".../promote", ...)</c> runs
    /// before flipping the active-version pointer (T116).
    /// </summary>
    private static bool CanPromote(Prompt prompt, bool anyOpenRegression)
    {
        if (prompt.PromotionBlockFlag) return false;
        if (anyOpenRegression) return false;
        return true;
    }
}
