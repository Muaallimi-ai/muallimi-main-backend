using System.Text.Json;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Domain.ProviderBindings;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract;

/// <summary>
/// T114 (US7, FR-023) — Red-team result persistence contract. The
/// persistence handler in main-backend consumes
/// <c>ai.tutor.redteam.run.completed</c> and:
///  - writes one <see cref="RedTeamEvaluationResult"/> row per run
///    (idempotent on <c>ResultId</c>),
///  - propagates <c>promotion_block_flag</c> to every
///    <see cref="Prompt"/> and <see cref="ProviderAdapterBinding"/>
///    referenced by <c>config_under_test</c>, and
///  - clears the flag on a subsequent passing run.
///
/// The tests exercise the row-shape invariants that the handler relies on
/// without standing up a live DbContext (the integration test
/// <c>RegressionPromotionBlockTests</c> covers the end-to-end gate and
/// the unit-test suite for the handler owns the DbContext write path).
/// </summary>
public class RedTeamResultPersistenceTests
{
    [Fact]
    public void Result_Row_Captures_Run_Summary_And_Regressions_Json()
    {
        var envelope = BuildEnvelope(promotionBlock: true, regressions: new[] { "hsc-002-en", "spl-002-en" });
        var row = ToRow(envelope);

        Assert.NotEqual(Guid.Empty, row.ResultId);
        Assert.Equal(envelope.ResultId, row.ResultId);
        Assert.Equal(envelope.ScenarioSetVersion, row.SetVersion);
        Assert.Equal(envelope.PassCount, row.PassCount);
        Assert.Equal(envelope.FailCount, row.FailCount);
        Assert.True(row.PromotionBlockFlag);

        var regressions = JsonSerializer.Deserialize<string[]>(row.Regressions);
        Assert.NotNull(regressions);
        Assert.Contains("hsc-002-en", regressions!);
        Assert.Contains("spl-002-en", regressions!);
    }

    [Fact]
    public void Propagates_PromotionBlockFlag_To_Affected_Prompts()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        var envelope = BuildEnvelope(
            promotionBlock: true,
            regressions: new[] { "pi-002-en" },
            prompts: new[] { prompt });

        PropagateFlags(envelope, new[] { prompt }, Array.Empty<ProviderAdapterBinding>());

        Assert.True(prompt.PromotionBlockFlag);
    }

    [Fact]
    public void Propagates_PromotionBlockFlag_To_Affected_ProviderBindings()
    {
        var binding = ProviderAdapterBinding.Create(
            capability: Capabilities.LlmStronger,
            environment: Environments.Local,
            curriculumScope: null,
            providerIdentifier: "local-stronger");

        var envelope = BuildEnvelope(
            promotionBlock: true,
            regressions: new[] { "io-002-en" },
            bindings: new[] { binding });

        PropagateFlags(envelope, Array.Empty<Prompt>(), new[] { binding });

        Assert.True(binding.PromotionBlockFlag);
    }

    [Fact]
    public void Passing_Run_Clears_The_Flag_On_Affected_Rows()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        prompt.ApplyPromotionBlock();
        var binding = ProviderAdapterBinding.Create(
            capability: Capabilities.LlmStronger,
            environment: Environments.Local,
            curriculumScope: null,
            providerIdentifier: "local-stronger");
        binding.PromotionBlockFlag = true;

        var passing = BuildEnvelope(
            promotionBlock: false,
            regressions: Array.Empty<string>(),
            prompts: new[] { prompt },
            bindings: new[] { binding });

        PropagateFlags(passing, new[] { prompt }, new[] { binding });

        Assert.False(prompt.PromotionBlockFlag);
        Assert.False(binding.PromotionBlockFlag);
    }

    [Fact]
    public void Result_Row_Preserves_CorrelationId_For_Incident_Investigation()
    {
        var envelope = BuildEnvelope(promotionBlock: false, regressions: Array.Empty<string>());
        var withCorrelation = envelope with { CorrelationId = "corr-redteam-42" };
        var row = ToRow(withCorrelation);

        Assert.Equal("corr-redteam-42", row.CorrelationId);
    }

    private static RedTeamRunCompletedEnvelope BuildEnvelope(
        bool promotionBlock,
        IReadOnlyList<string> regressions,
        IReadOnlyList<Prompt>? prompts = null,
        IReadOnlyList<ProviderAdapterBinding>? bindings = null)
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
                (prompts ?? Array.Empty<Prompt>())
                    .Select(p => new RedTeamPromptBinding(p.PromptId, p.ActiveVersionId)).ToArray(),
                (bindings ?? Array.Empty<ProviderAdapterBinding>())
                    .Select(b => new RedTeamAdapterBinding(b.BindingId, b.Capability)).ToArray()),
            ArtifactKey: $"redteam/artifacts/readiness-v1/1.0.0/{Guid.NewGuid():N}.json",
            CorrelationId: null);

    private static RedTeamEvaluationResult ToRow(RedTeamRunCompletedEnvelope envelope)
        => new()
        {
            ResultId = envelope.ResultId,
            SetId = DeterministicGuid(envelope.ScenarioSetId),
            SetVersion = envelope.ScenarioSetVersion,
            RunAt = envelope.EvaluatedAt,
            PassCount = envelope.PassCount,
            FailCount = envelope.FailCount,
            Regressions = JsonSerializer.Serialize(envelope.Regressions),
            PromotionBlockFlag = envelope.PromotionBlockFlag,
            CorrelationId = envelope.CorrelationId,
        };

    private static void PropagateFlags(
        RedTeamRunCompletedEnvelope envelope,
        IReadOnlyList<Prompt> prompts,
        IReadOnlyList<ProviderAdapterBinding> bindings)
    {
        var promptIds = envelope.ConfigUnderTest.PromptBindings.Select(b => b.PromptId).ToHashSet();
        var bindingIds = envelope.ConfigUnderTest.AdapterBindings.Select(b => b.BindingId).ToHashSet();
        foreach (var p in prompts.Where(p => promptIds.Contains(p.PromptId)))
        {
            if (envelope.PromotionBlockFlag) p.ApplyPromotionBlock();
            else p.ClearPromotionBlock();
        }
        foreach (var b in bindings.Where(b => bindingIds.Contains(b.BindingId)))
        {
            b.PromotionBlockFlag = envelope.PromotionBlockFlag;
        }
    }

    private static Guid DeterministicGuid(string input)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);
        return new Guid(guidBytes);
    }
}
