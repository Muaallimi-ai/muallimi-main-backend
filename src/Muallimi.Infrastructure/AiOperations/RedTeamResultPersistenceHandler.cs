using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.AiOperations;
using Muallimi.Domain.AiOperations;
using Muallimi.Domain.PromptAudit.Entities;
using Muallimi.Domain.ProviderBindings;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.AiOperations;

/// <summary>
/// T114 (US7) — Consumes <c>ai.tutor.redteam.run.completed</c> and writes
/// the canonical <see cref="RedTeamEvaluationResult"/> row. When the run
/// reports regressions (or any fail), the handler propagates
/// <c>promotion_block_flag=true</c> to every <see cref="Prompt"/> and
/// <see cref="ProviderAdapterBinding"/> referenced by
/// <c>config_under_test</c>. A subsequent passing run for the same scenario
/// set clears the flag on those same records. This is what
/// <c>PromotionRegistryEndpoints.Promote</c> reads when enforcing FR-023.
///
/// Idempotent on <c>ResultId</c>: replaying the same envelope is a no-op.
/// </summary>
public class RedTeamResultPersistenceHandler
{
    private readonly MuallimiDbContext _db;

    public RedTeamResultPersistenceHandler(MuallimiDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task HandleAsync(RedTeamRunCompletedEnvelope envelope, CancellationToken ct = default)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        var exists = await _db.RedTeamEvaluationResults
            .AsNoTracking()
            .AnyAsync(r => r.ResultId == envelope.ResultId, ct);
        if (exists) return;

        if (!Guid.TryParse(envelope.ScenarioSetId, out var setIdGuid))
            setIdGuid = DeterministicGuid(envelope.ScenarioSetId);

        var row = new RedTeamEvaluationResult
        {
            ResultId = envelope.ResultId,
            SetId = setIdGuid,
            SetVersion = envelope.ScenarioSetVersion,
            RunAt = envelope.EvaluatedAt,
            PassCount = envelope.PassCount,
            FailCount = envelope.FailCount,
            Regressions = JsonSerializer.Serialize(envelope.Regressions ?? Array.Empty<string>()),
            PromotionBlockFlag = envelope.PromotionBlockFlag,
            CorrelationId = envelope.CorrelationId,
        };
        _db.RedTeamEvaluationResults.Add(row);

        await PropagateFlagsAsync(envelope, ct);

        await _db.SaveChangesAsync(ct);
    }

    private async Task PropagateFlagsAsync(RedTeamRunCompletedEnvelope envelope, CancellationToken ct)
    {
        var promptIds = envelope.ConfigUnderTest?.PromptBindings?
            .Select(b => b.PromptId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray() ?? Array.Empty<Guid>();

        var adapterIds = envelope.ConfigUnderTest?.AdapterBindings?
            .Select(b => b.BindingId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray() ?? Array.Empty<Guid>();

        if (promptIds.Length > 0)
        {
            var prompts = await _db.Prompts.Where(p => promptIds.Contains(p.PromptId)).ToListAsync(ct);
            foreach (var prompt in prompts)
            {
                if (envelope.PromotionBlockFlag)
                    prompt.ApplyPromotionBlock();
                else
                    prompt.ClearPromotionBlock();
            }
        }

        if (adapterIds.Length > 0)
        {
            var bindings = await _db.ProviderAdapterBindings.Where(b => adapterIds.Contains(b.BindingId)).ToListAsync(ct);
            foreach (var binding in bindings)
            {
                binding.PromotionBlockFlag = envelope.PromotionBlockFlag;
                binding.UpdateFallbackChain(binding.ReadFallbackChain());
            }
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
