using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Services;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// Emits the parent's <see cref="ParentProfile"/> id as the <c>parent</c>
/// entry inside the JWT's <c>profile_ids</c> claim.
///
/// Self-healing: if the user holds the <c>parent</c> role but no
/// <see cref="ParentProfile"/> row exists yet (legacy account that
/// registered before the registration flow created one), the contributor
/// inserts a row with sensible defaults and returns its id. This makes
/// the next access token carry the correct claim with no manual backfill.
/// </summary>
public sealed class ParentProfileIdContributor : IProfileIdContributor
{
    private readonly MuallimiDbContext _db;

    public ParentProfileIdContributor(MuallimiDbContext db)
    {
        _db = db;
    }

    public string Key => "parent";

    public async Task<Guid?> ResolveAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        // Global tenant filter is ignored — token issuance runs before the
        // request's tenant context is bound, so we pin tenantId explicitly.
        var existing = await _db.ParentProfiles
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId && p.TenantId == tenantId)
            .Select(p => (Guid?)p.ParentProfileId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existing is not null) return existing;

        // No row — only create one if the user actually holds the parent role
        // in this tenant. Without this guard, every login would try to insert
        // a parent profile for non-parent users.
        var hasParentRole = await (
            from ur in _db.IdentityUserRoles.IgnoreQueryFilters()
            join r in _db.IdentityRoles.IgnoreQueryFilters() on ur.RoleId equals r.Id
            where ur.UserId == userId && ur.TenantId == tenantId && r.Name == "parent"
            select 1
        ).AnyAsync(ct).ConfigureAwait(false);
        if (!hasParentRole) return null;

        var now = DateTime.UtcNow;
        var profile = new ParentProfile
        {
            ParentProfileId = Guid.NewGuid(),
            TenantId = tenantId,
            IdentityId = userId,
            UserId = userId,
            PreferredLanguage = "ar",
            Locale = "ar-EG",
            Timezone = "Africa/Cairo",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ParentProfiles.Add(profile);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return profile.ParentProfileId;
    }
}
