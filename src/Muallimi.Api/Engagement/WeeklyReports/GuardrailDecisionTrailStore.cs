using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T022 — GuardrailDecisionTrailStore.
///
/// Every Phase 4 natural-language artefact (weekly report summary, focus-
/// area rationale, intervention prompt) flows through the Phase 2 guardrail
/// chain and leaves behind a decision trail. This store persists the raw
/// chain output so incidents can be traced back to the specific guardrail
/// stages, prompt version, and final outcome that produced the text.
///
/// Artefact writers call <see cref="RecordAsync"/> inside the same unit of
/// work as the artefact insert, and set
/// <c>guardrail_decision_trail_id</c> on the artefact row to the returned
/// id so the FK is atomic with the text.
/// </summary>
public static class GuardrailDecisionTrailArtefactKinds
{
    public const string WeeklyReportSummary = "weekly_report_summary";
    public const string FocusAreaRationale = "focus_area_rationale";
    public const string InterventionPrompt = "intervention_prompt";
}

public interface IGuardrailDecisionTrailStore
{
    Task<Guid> RecordAsync(
        Guid tenantId,
        string artefactKind,
        Guid artefactId,
        string promptKey,
        string chainOutputJson,
        string finalStage,
        string language,
        string correlationId,
        CancellationToken ct = default);
}

public sealed class GuardrailDecisionTrailStore : IGuardrailDecisionTrailStore
{
    private readonly MuallimiDbContext _db;

    public GuardrailDecisionTrailStore(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<Guid> RecordAsync(
        Guid tenantId,
        string artefactKind,
        Guid artefactId,
        string promptKey,
        string chainOutputJson,
        string finalStage,
        string language,
        string correlationId,
        CancellationToken ct = default)
    {
        var row = new GuardrailDecisionTrail
        {
            GuardrailDecisionTrailId = Guid.NewGuid(),
            TenantId = tenantId,
            ArtefactKind = artefactKind,
            ArtefactId = artefactId,
            PromptKey = promptKey,
            ChainOutput = string.IsNullOrWhiteSpace(chainOutputJson) ? "{}" : chainOutputJson,
            FinalStage = finalStage,
            Language = language,
            CorrelationId = correlationId,
            CapturedAt = DateTime.UtcNow,
        };
        _db.GuardrailDecisionTrails.Add(row);
        await _db.SaveChangesAsync(ct);
        return row.GuardrailDecisionTrailId;
    }
}

public static class GuardrailDecisionTrailStoreServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4GuardrailDecisionTrailStore(this IServiceCollection services)
    {
        services.AddScoped<IGuardrailDecisionTrailStore, GuardrailDecisionTrailStore>();
        return services;
    }
}
