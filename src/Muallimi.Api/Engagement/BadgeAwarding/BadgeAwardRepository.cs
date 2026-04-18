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
/// T036 (US4) — <see cref="BadgeAward"/> repository.
///
/// Awards are unique by
/// (tenant_id, student_id, badge_criterion_id, badge_criterion_version) so
/// re-evaluation never double-awards the same badge version to the same
/// student.
/// </summary>
public interface IBadgeAwardRepository
{
    Task<bool> ExistsAsync(
        Guid tenantId,
        Guid studentId,
        Guid badgeCriterionId,
        string badgeCriterionVersion,
        CancellationToken ct = default);

    Task AddAsync(BadgeAward award, CancellationToken ct = default);

    Task<IReadOnlyList<BadgeAward>> ForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default);
}

public sealed class BadgeAwardRepository : IBadgeAwardRepository
{
    private readonly MuallimiDbContext _db;

    public BadgeAwardRepository(MuallimiDbContext db) => _db = db;

    public Task<bool> ExistsAsync(
        Guid tenantId,
        Guid studentId,
        Guid badgeCriterionId,
        string badgeCriterionVersion,
        CancellationToken ct = default)
        => _db.BadgeAwards
            .IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId
                           && a.StudentId == studentId
                           && a.BadgeCriterionId == badgeCriterionId
                           && a.BadgeCriterionVersion == badgeCriterionVersion,
                ct);

    public Task AddAsync(BadgeAward award, CancellationToken ct = default)
    {
        _db.BadgeAwards.Add(award);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<BadgeAward>> ForStudentAsync(
        Guid tenantId,
        Guid studentId,
        CancellationToken ct = default)
    {
        return await _db.BadgeAwards
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.StudentId == studentId)
            .ToListAsync(ct);
    }
}

public static class BadgeAwardRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4BadgeAwardRepository(this IServiceCollection services)
    {
        services.AddScoped<IBadgeAwardRepository, BadgeAwardRepository>();
        return services;
    }
}
