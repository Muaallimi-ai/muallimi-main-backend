using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.AiOperations;

/// <summary>
/// T114/T117 (US7) — Operator-facing query surface over persisted
/// <c>RedTeamEvaluationResult</c> rows. The frontend red-team results view
/// reads from these endpoints to render per-run pass/fail,
/// <c>promotion_block_flag</c> state, and the regression list.
///
/// Routes:
///  - GET /internal/redteam/results            — list (newest first)
///  - GET /internal/redteam/results/{resultId} — single result + regressions
///  - GET /internal/redteam/gated              — prompts and bindings currently
///    carrying a <c>promotion_block_flag</c> (T116)
/// </summary>
public static class RedTeamResultsEndpoints
{
    public const string ResultsRoute = "/internal/redteam/results";
    public const string ResultByIdRoute = "/internal/redteam/results/{resultId:guid}";
    public const string GatedRoute = "/internal/redteam/gated";

    public static void MapRedTeamResultsEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ResultsRoute, async (
            MuallimiDbContext db,
            string? setVersion,
            bool? blockedOnly,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
        {
            var query = db.RedTeamEvaluationResults.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(setVersion))
                query = query.Where(r => r.SetVersion == setVersion);
            if (blockedOnly == true)
                query = query.Where(r => r.PromotionBlockFlag);

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(r => r.RunAt)
                .Skip(Math.Max(0, (page - 1) * pageSize))
                .Take(Math.Clamp(pageSize, 1, 200))
                .ToListAsync(ct);

            return Results.Ok(new
            {
                items = items.Select(r => new
                {
                    result_id = r.ResultId,
                    set_id = r.SetId,
                    set_version = r.SetVersion,
                    run_at = r.RunAt,
                    pass_count = r.PassCount,
                    fail_count = r.FailCount,
                    regressions = r.Regressions,
                    promotion_block_flag = r.PromotionBlockFlag,
                    correlation_id = r.CorrelationId,
                }),
                total,
                page,
                page_size = pageSize,
            });
        })
        .WithName("ListRedTeamResults")
        .WithTags("RedTeam");

        routes.MapGet(ResultByIdRoute, async (Guid resultId, MuallimiDbContext db, CancellationToken ct) =>
        {
            var result = await db.RedTeamEvaluationResults.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ResultId == resultId, ct);
            if (result is null) return Results.NotFound(new { error = "Result not found." });

            return Results.Ok(new
            {
                result_id = result.ResultId,
                set_id = result.SetId,
                set_version = result.SetVersion,
                run_at = result.RunAt,
                pass_count = result.PassCount,
                fail_count = result.FailCount,
                regressions = result.Regressions,
                promotion_block_flag = result.PromotionBlockFlag,
                correlation_id = result.CorrelationId,
            });
        })
        .WithName("GetRedTeamResult")
        .WithTags("RedTeam");

        routes.MapGet(GatedRoute, async (MuallimiDbContext db, CancellationToken ct) =>
        {
            var gatedPrompts = await db.Prompts.AsNoTracking()
                .Where(p => p.PromotionBlockFlag)
                .Select(p => new
                {
                    prompt_id = p.PromptId,
                    name = p.Name,
                    scope = p.Scope,
                    active_version_id = p.ActiveVersionId,
                })
                .ToListAsync(ct);

            var gatedBindings = await db.ProviderAdapterBindings.AsNoTracking()
                .Where(b => b.PromotionBlockFlag)
                .Select(b => new
                {
                    binding_id = b.BindingId,
                    capability = b.Capability,
                    environment = b.Environment,
                    curriculum_scope = b.CurriculumScope,
                    provider_identifier = b.ProviderIdentifier,
                })
                .ToListAsync(ct);

            return Results.Ok(new
            {
                prompts = gatedPrompts,
                adapter_bindings = gatedBindings,
            });
        })
        .WithName("ListRedTeamGatedConfiguration")
        .WithTags("RedTeam");
    }
}
