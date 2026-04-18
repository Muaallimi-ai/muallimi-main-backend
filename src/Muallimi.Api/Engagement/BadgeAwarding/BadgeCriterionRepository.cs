using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.BadgeAwarding;

/// <summary>
/// T036 (US4) — <see cref="BadgeCriterion"/> repository.
///
/// Read-only surface over the versioned catalogue seeded in
/// <c>BadgeCriterionV1</c>. Criteria are tenant-agnostic; a row is globally
/// active until <c>retired_at</c> is set.
/// </summary>
public interface IBadgeCriterionRepository
{
    Task<IReadOnlyList<BadgeCriterion>> ActiveAsync(CancellationToken ct = default);
}

public sealed class BadgeCriterionRepository : IBadgeCriterionRepository
{
    private readonly MuallimiDbContext _db;

    public BadgeCriterionRepository(MuallimiDbContext db) => _db = db;

    public async Task<IReadOnlyList<BadgeCriterion>> ActiveAsync(CancellationToken ct = default)
    {
        return await _db.BadgeCriteria
            .Where(c => c.RetiredAt == null)
            .ToListAsync(ct);
    }
}

public static class BadgeCriterionRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4BadgeCriterionRepository(this IServiceCollection services)
    {
        services.AddScoped<IBadgeCriterionRepository, BadgeCriterionRepository>();
        return services;
    }
}
