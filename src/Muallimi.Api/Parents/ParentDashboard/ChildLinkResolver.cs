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
/// T019 — ChildLinkResolver.
///
/// Enforces tenant + active-link filtering on every parent-facing query.
/// Every dashboard, weekly report, notification, preference, and at-risk
/// read MUST resolve the target child through <see cref="ResolveAsync"/>;
/// an unlinked or inactive child is refused with a 404 (never a 403 — the
/// distinction would leak cross-family existence).
///
/// The underlying EF query filter already restricts to the ambient tenant;
/// this resolver adds the parent + child-link check on top so a co-parent
/// in the same tenant cannot see the other parent's children unless an
/// active <c>ChildLink</c> exists.
/// </summary>
public interface IChildLinkResolver
{
    Task<ChildLink?> ResolveAsync(Guid parentProfileId, Guid childId, CancellationToken ct = default);
    Task<IReadOnlyList<ChildLink>> ListForParentAsync(Guid parentProfileId, CancellationToken ct = default);
}

public sealed class ChildLinkResolver : IChildLinkResolver
{
    private readonly MuallimiDbContext _db;

    public ChildLinkResolver(MuallimiDbContext db)
    {
        _db = db;
    }

    public async Task<ChildLink?> ResolveAsync(Guid parentProfileId, Guid childId, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        return await _db.ChildLinks
            .Where(l => l.ParentProfileId == parentProfileId && l.StudentId == childId)
            .Where(l => l.EffectiveStart <= today && (l.EffectiveEnd == null || l.EffectiveEnd >= today))
            .OrderByDescending(l => l.EffectiveStart)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ChildLink>> ListForParentAsync(Guid parentProfileId, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        return await _db.ChildLinks
            .Where(l => l.ParentProfileId == parentProfileId)
            .Where(l => l.EffectiveStart <= today && (l.EffectiveEnd == null || l.EffectiveEnd >= today))
            .OrderBy(l => l.EffectiveStart)
            .ToListAsync(ct);
    }
}

public static class ChildLinkResolverServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ChildLinkResolver(this IServiceCollection services)
    {
        services.AddScoped<IChildLinkResolver, ChildLinkResolver>();
        return services;
    }
}
