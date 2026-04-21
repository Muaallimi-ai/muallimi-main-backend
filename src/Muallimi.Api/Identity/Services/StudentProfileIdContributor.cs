using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Services;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Services;

/// <summary>
/// Emits the student's <see cref="Muallimi.Domain.StudentExperience.StudentProfile"/>
/// id as the <c>student</c> entry inside the JWT's <c>profile_ids</c>
/// claim. Returns null when the user has no linked student profile
/// (e.g. pre-backfill legacy accounts or parents).
/// </summary>
public sealed class StudentProfileIdContributor : IProfileIdContributor
{
    private readonly MuallimiDbContext _db;

    public StudentProfileIdContributor(MuallimiDbContext db)
    {
        _db = db;
    }

    public string Key => "student";

    public async Task<Guid?> ResolveAsync(Guid userId, Guid tenantId, CancellationToken ct)
    {
        // Global tenant filter is ignored because tenantId is passed
        // explicitly by the token pipeline — the request has not yet
        // bound an ITenantContext at login time.
        return await _db.StudentProfiles
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId && p.TenantId == tenantId)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
