using System;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// Contributor for a single domain profile entity that is keyed 1:1 to
/// an authenticated <see cref="Muallimi.Domain.Identity.Entities.User"/>.
/// The resolved id is emitted on the JWT as an entry in the
/// <c>profile_ids</c> claim (see
/// <see cref="JwtTokenService.GenerateAccessToken"/>).
///
/// Add a new domain profile → implement this once, register the impl
/// in DI, done. The issuer, refresh path, and frontend consumers
/// require no changes.
/// </summary>
public interface IProfileIdContributor
{
    /// <summary>
    /// Stable key emitted inside the <c>profile_ids</c> claim object.
    /// Use lowercase snake-case (e.g. <c>"student"</c>, <c>"teacher"</c>,
    /// <c>"school_admin"</c>). Once shipped, this key is part of the
    /// <c>identity.claims</c> contract and MUST NOT change.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Returns the profile id for the given user, or <c>null</c> if the
    /// user has no profile of this kind.
    /// </summary>
    Task<Guid?> ResolveAsync(Guid userId, Guid tenantId, CancellationToken ct);
}
