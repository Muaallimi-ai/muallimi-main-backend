using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// Aggregates every registered <see cref="IProfileIdContributor"/> into
/// a single keyed dictionary that is stamped onto the JWT as the
/// <c>profile_ids</c> claim. Used by the login, refresh, and
/// impersonation pipelines so every issued token carries the caller's
/// full set of domain profile ids.
///
/// Missing profiles are omitted (null Guid → no entry). An empty
/// dictionary is a legitimate result for users with no domain profile
/// (e.g. platform operators) — the issuer still emits an empty
/// <c>{}</c> claim so consumers can safely index without a defensive
/// null check.
/// </summary>
public interface IProfileIdsResolver
{
    Task<IReadOnlyDictionary<string, Guid>> ResolveAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct);
}

public sealed class ProfileIdsResolver : IProfileIdsResolver
{
    private readonly IEnumerable<IProfileIdContributor> _contributors;

    public ProfileIdsResolver(IEnumerable<IProfileIdContributor> contributors)
    {
        _contributors = contributors;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> ResolveAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct)
    {
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var contributor in _contributors.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var id = await contributor.ResolveAsync(userId, tenantId, ct).ConfigureAwait(false);
            if (id.HasValue && id.Value != Guid.Empty)
            {
                result[contributor.Key] = id.Value;
            }
        }
        return result;
    }
}
