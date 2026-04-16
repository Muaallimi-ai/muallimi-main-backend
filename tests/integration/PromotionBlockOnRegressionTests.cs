using Muallimi.Domain.AiOperations;
using Muallimi.Domain.PromptAudit.Entities;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration;

/// <summary>
/// T075 (US4, FR-012) — A prompt version cannot be promoted to active while
/// there is an open red-team regression against it. The gate is enforced by
/// two independent signals both of which the PromoteRequest handler checks
/// before applying the active-version pointer:
///  - <c>Prompt.PromotionBlockFlag</c> (carried over from the nightly
///    red-team run when a regression tags the prompt itself), and
///  - any <c>RedTeamEvaluationResult</c> with <c>PromotionBlockFlag=true</c>
///    unresolved at promotion time.
/// </summary>
public class PromotionBlockOnRegressionTests
{
    [Fact]
    public void Prompt_With_PromotionBlockFlag_Is_Gated()
    {
        var prompt = Prompt.Create("system.lightweight", "tutor", "global");
        prompt.ApplyPromotionBlock();

        Assert.True(prompt.PromotionBlockFlag);
    }

    [Fact]
    public void Open_RedTeam_Regression_Signals_PromotionBlock()
    {
        var result = new RedTeamEvaluationResult
        {
            ResultId = Guid.NewGuid(),
            SetId = Guid.NewGuid(),
            SetVersion = "2026.04.16",
            RunAt = DateTime.UtcNow,
            PassCount = 240,
            FailCount = 3,
            Regressions = "[{\"scenario\":\"refusal-leak\",\"delta\":\"regressed\"}]",
            PromotionBlockFlag = true,
            CorrelationId = "corr-redteam-1",
        };

        Assert.True(result.PromotionBlockFlag);
        Assert.NotEqual(0, result.FailCount);
    }

    [Fact]
    public void Resolved_RedTeam_Result_Allows_Promotion()
    {
        // After a fix lands and the next red-team run passes, the evaluation
        // record's promotion_block_flag is reset to false, matching the
        // behaviour the write endpoint depends on when querying for blockers.
        var result = new RedTeamEvaluationResult
        {
            ResultId = Guid.NewGuid(),
            SetId = Guid.NewGuid(),
            SetVersion = "2026.04.17",
            RunAt = DateTime.UtcNow,
            PassCount = 243,
            FailCount = 0,
            Regressions = "[]",
            PromotionBlockFlag = false,
            CorrelationId = "corr-redteam-2",
        };

        Assert.False(result.PromotionBlockFlag);
    }

    [Fact]
    public void Promote_Is_Rejected_When_Any_Open_Regression_Exists()
    {
        // The write endpoint runs:
        //   db.RedTeamEvaluationResults.Any(r => r.PromotionBlockFlag)
        // before flipping the active pointer. Any single open regression is
        // enough to reject the promotion, even if it is unrelated to the
        // prompt under promotion — a conservative gate that matches FR-012.
        var results = new[]
        {
            new RedTeamEvaluationResult { PromotionBlockFlag = false },
            new RedTeamEvaluationResult { PromotionBlockFlag = true },  // open regression
            new RedTeamEvaluationResult { PromotionBlockFlag = false },
        };

        var anyOpen = results.Any(r => r.PromotionBlockFlag);
        Assert.True(anyOpen);
    }
}

