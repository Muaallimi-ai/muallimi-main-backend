using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Application.Identity.Credentials;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Credentials;

/// <summary>
/// Phase 9 Phase 4 — single-parent recipient resolver. Returns the
/// parent User.Id from <c>User.ManagedByUserId</c> for parent-managed
/// children, filtering out:
///   - school-managed students (manager.AccountType != Personal)
///   - archived parents
/// so the notifier never fires for B2B accounts or deleted users.
///
/// TODO (B2B / multi-guardian): when <c>ChildGuardian</c> link tables
/// land, replace the single-element list with a query that iterates
/// every active guardian. The interface contract is already
/// pluralized — callers don't change.
/// </summary>
public sealed class ManagedUserNotificationRecipients : IManagedUserNotificationRecipients
{
    private readonly MuallimiDbContext _db;

    public ManagedUserNotificationRecipients(MuallimiDbContext db) => _db = db;

    public async Task<IReadOnlyList<Guid>> GetRecipientsAsync(
        Guid managedUserId,
        CredentialAuditEventKind kind,
        CancellationToken ct = default)
    {
        var child = await _db.IdentityUsers.IgnoreQueryFilters()
            .Where(u => u.Id == managedUserId)
            .Select(u => new { u.AccountType, u.ManagedByUserId, u.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (child is null
            || child.AccountType != AccountType.Managed
            || child.ManagedByUserId is null
            || child.Status == UserStatus.Archived)
        {
            return Array.Empty<Guid>();
        }

        var parentId = child.ManagedByUserId.Value;
        var parent = await _db.IdentityUsers.IgnoreQueryFilters()
            .Where(u => u.Id == parentId)
            .Select(u => new { u.AccountType, u.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (parent is null
            || parent.AccountType != AccountType.Personal
            || parent.Status == UserStatus.Archived)
        {
            // School-managed (Personal=false) or removed — no parent
            // recipient. School-admin notifications are a future B2B
            // story handled by a separate resolver.
            return Array.Empty<Guid>();
        }

        return new[] { parentId };
    }
}
