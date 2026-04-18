using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.ParentDashboard;

/// <summary>
/// T070 (US2) — <see cref="ChildLink"/> repository.
///
/// The parent dashboard filters every state row by
/// <c>(tenant_id, parent_profile_id, child_id)</c> using an ACTIVE
/// <c>ChildLink</c>. This repository is the single read path for that
/// filter; <see cref="ChildLinkResolver"/> (T019) layers the single-child
/// resolve on top. Co-parent and sibling shapes are both supported — the
/// repository never assumes a single child per parent.
/// </summary>
public interface IChildLinkRepository
{
    Task<IReadOnlyList<ChildLink>> ListActiveForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        CancellationToken ct = default);

    Task<ChildLink?> GetActiveAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid childId,
        CancellationToken ct = default);
}

public sealed class ChildLinkRepository : IChildLinkRepository
{
    private readonly MuallimiDbContext _db;

    public ChildLinkRepository(MuallimiDbContext db) => _db = db;

    public async Task<IReadOnlyList<ChildLink>> ListActiveForParentAsync(
        Guid tenantId,
        Guid parentProfileId,
        CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        return await _db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.ParentProfileId == parentProfileId)
            .Where(l => l.EffectiveStart <= today && (l.EffectiveEnd == null || l.EffectiveEnd >= today))
            .OrderBy(l => l.EffectiveStart)
            .ToListAsync(ct);
    }

    public Task<ChildLink?> GetActiveAsync(
        Guid tenantId,
        Guid parentProfileId,
        Guid childId,
        CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        return _db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId
                        && l.ParentProfileId == parentProfileId
                        && l.StudentId == childId)
            .Where(l => l.EffectiveStart <= today && (l.EffectiveEnd == null || l.EffectiveEnd >= today))
            .OrderByDescending(l => l.EffectiveStart)
            .FirstOrDefaultAsync(ct);
    }
}

public static class ChildLinkRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ChildLinkRepository(this IServiceCollection services)
    {
        services.AddScoped<IChildLinkRepository, ChildLinkRepository>();
        return services;
    }
}
