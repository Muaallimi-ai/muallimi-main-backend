using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.InterventionPrompts;

/// <summary>
/// T143 (US8) — <see cref="InterventionPrompt"/> repository.
///
/// Intervention prompts are tenant + student scoped. Each row is anchored to
/// its originating <see cref="AtRiskFlag"/> (or focus area, in future use)
/// and to the guardrail decision trail row produced by the Phase 2 chain.
/// </summary>
public interface IInterventionPromptRepository
{
    Task<InterventionPrompt?> GetByIdAsync(Guid tenantId, Guid interventionPromptId, CancellationToken ct = default);

    Task<InterventionPrompt?> GetByFlagIdAsync(Guid tenantId, Guid atRiskFlagId, CancellationToken ct = default);

    Task AddAsync(InterventionPrompt prompt, CancellationToken ct = default);
}

public sealed class InterventionPromptRepository : IInterventionPromptRepository
{
    private readonly MuallimiDbContext _db;

    public InterventionPromptRepository(MuallimiDbContext db) => _db = db;

    public Task<InterventionPrompt?> GetByIdAsync(Guid tenantId, Guid interventionPromptId, CancellationToken ct = default)
    {
        return _db.InterventionPrompts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.InterventionPromptId == interventionPromptId)
            .FirstOrDefaultAsync(ct);
    }

    public Task<InterventionPrompt?> GetByFlagIdAsync(Guid tenantId, Guid atRiskFlagId, CancellationToken ct = default)
    {
        return _db.InterventionPrompts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.OriginatingFlagId == atRiskFlagId)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public Task AddAsync(InterventionPrompt prompt, CancellationToken ct = default)
    {
        _db.InterventionPrompts.Add(prompt);
        return Task.CompletedTask;
    }
}

public static class InterventionPromptRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4InterventionPromptRepository(this IServiceCollection services)
    {
        services.AddScoped<IInterventionPromptRepository, InterventionPromptRepository>();
        return services;
    }
}
