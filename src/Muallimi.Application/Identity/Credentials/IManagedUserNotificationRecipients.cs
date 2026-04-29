using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Muallimi.Application.Identity.Credentials;

/// <summary>
/// Resolves the user IDs that should receive a notification when a
/// credential event fires on a managed user (today: child of a parent;
/// tomorrow: student of a school).
///
/// The resolver returns recipients tailored to the event kind. For
/// example, a parent-initiated reset does NOT notify the parent who
/// performed it (they already know) — but a child self-change DOES
/// notify all guardians.
///
/// Today the only managed-user shape is parent-managed children, so
/// the implementation iterates a single-element list (the parent).
/// When <c>ChildGuardian</c> link tables ship for multi-guardian
/// families and B2B school-admin notifications, this method gets a
/// richer return without touching any callers.
/// </summary>
public interface IManagedUserNotificationRecipients
{
    Task<IReadOnlyList<Guid>> GetRecipientsAsync(
        Guid managedUserId,
        CredentialAuditEventKind kind,
        CancellationToken ct = default);
}
