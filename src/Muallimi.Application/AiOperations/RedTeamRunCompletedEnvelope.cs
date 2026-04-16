namespace Muallimi.Application.AiOperations;

/// <summary>
/// T114 (US7) — Consumer-side mirror of
/// <c>ai.tutor.redteam.run.completed</c> emitted by ai-service after a
/// red-team evaluation finishes. The shape tracks
/// <c>specs/004-ai-tutor-rag/contracts/redteam-evaluation-contract.md</c>:
/// the event carries the result summary plus the configuration snapshot
/// that was evaluated, which is what the persistence handler needs to
/// flip <c>promotion_block_flag</c> on affected
/// <c>Prompt</c> / <c>ProviderAdapterBinding</c> rows (FR-023).
/// </summary>
public record RedTeamRunCompletedEnvelope(
    string EventId,
    Guid ResultId,
    string ScenarioSetId,
    string ScenarioSetVersion,
    DateTime EvaluatedAt,
    int PassCount,
    int FailCount,
    IReadOnlyList<string> Regressions,
    bool PromotionBlockFlag,
    RedTeamConfigSnapshot ConfigUnderTest,
    string ArtifactKey,
    string? CorrelationId);

public record RedTeamConfigSnapshot(
    IReadOnlyList<RedTeamPromptBinding> PromptBindings,
    IReadOnlyList<RedTeamAdapterBinding> AdapterBindings);

public record RedTeamPromptBinding(Guid PromptId, Guid? VersionId);

public record RedTeamAdapterBinding(Guid BindingId, string Capability);

/// <summary>
/// Minimal consumer; OnReceived is wired by the queue adapter or by tests.
/// Idempotent on <c>EventId</c> via the persistence handler.
/// </summary>
public class RedTeamRunCompletedConsumer
{
    public Func<RedTeamRunCompletedEnvelope, CancellationToken, Task>? OnReceived { get; set; }

    public Task HandleAsync(RedTeamRunCompletedEnvelope envelope, CancellationToken ct = default)
        => OnReceived is null ? Task.CompletedTask : OnReceived(envelope, ct);
}
