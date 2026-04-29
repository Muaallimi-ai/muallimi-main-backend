using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Identity.Credentials;
using Muallimi.Application.Identity.Notifications;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Parents;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Credentials;

/// <summary>
/// Phase 9 Phase 4 — single coordinator for the three credential
/// notification events:
///
///   1. <c>child_password_changed</c>           — 13+ child changed own password
///   2. <c>child_birthday_pin_eligible</c>      — child turned 8 (parent should add PIN)
///   3. <c>child_birthday_password_eligible</c> — child turned 13 (parent should upgrade)
///
/// All three events fan out to two surfaces:
///   - in-app inbox row (rendered as banner on parent home if &lt; 24h old)
///   - email via <see cref="IIdentityNotificationSender"/>
///
/// Per-day dedup is enforced: if an inbox row for the same
/// (parent, child, kind) already exists in the last 24h, the
/// existing row's <c>CreatedAt</c> is bumped to "now" instead of
/// inserting a new one, and the email is suppressed. Result: a
/// single row per child per kind per day, regardless of how many
/// times the underlying event fires.
///
/// Reuses the existing <see cref="IParentNotificationRepository"/> and
/// <see cref="IIdentityNotificationSender"/> — no parallel
/// notification path is introduced.
/// </summary>
public interface IChildCredentialNotifier
{
    Task NotifyChildPasswordChangedAsync(User child, string correlationId, CancellationToken ct = default);
    Task NotifyBirthdayPinEligibleAsync(User child, string correlationId, CancellationToken ct = default);
    Task NotifyBirthdayPasswordEligibleAsync(User child, string correlationId, CancellationToken ct = default);
}

public sealed class ChildCredentialNotifier : IChildCredentialNotifier
{
    /// <summary>Stable inbox `notification_kind` strings (also used by frontend banner filter).</summary>
    public const string KindChildPasswordChanged = "child_password_changed";
    public const string KindChildBirthdayPinEligible = "child_birthday_pin_eligible";
    public const string KindChildBirthdayPasswordEligible = "child_birthday_password_eligible";

    /// <summary>Dedup window: one inbox row + one email per child per kind per 24 hours.</summary>
    public static readonly TimeSpan DedupWindow = TimeSpan.FromHours(24);

    private readonly MuallimiDbContext _db;
    private readonly IManagedUserNotificationRecipients _recipients;
    private readonly IParentNotificationRepository _inbox;
    private readonly IIdentityNotificationSender _email;
    private readonly ILogger<ChildCredentialNotifier> _logger;

    public ChildCredentialNotifier(
        MuallimiDbContext db,
        IManagedUserNotificationRecipients recipients,
        IParentNotificationRepository inbox,
        IIdentityNotificationSender email,
        ILogger<ChildCredentialNotifier> logger)
    {
        _db = db;
        _recipients = recipients;
        _inbox = inbox;
        _email = email;
        _logger = logger;
    }

    // ── Public events ────────────────────────────────────────────────

    public Task NotifyChildPasswordChangedAsync(User child, string correlationId, CancellationToken ct = default)
        => FanOutAsync(child, KindChildPasswordChanged, CredentialAuditEventKind.ChildPasswordChangedSelf,
            (recipient, ctx) => _email.SendChildPasswordChangedByChildAsync(
                recipient, ctx.ChildName, ctx.ChildGrade, ctx.ChildUsername, DateTime.UtcNow, correlationId, ct),
            (ctx) => (
                ar: $"{ctx.ChildName} غيّر كلمة مرور حسابه على معلّمي.",
                en: $"{ctx.ChildName} changed their account password on Muaallimi."),
            correlationId, ct);

    public Task NotifyBirthdayPinEligibleAsync(User child, string correlationId, CancellationToken ct = default)
        => FanOutAsync(child, KindChildBirthdayPinEligible, CredentialAuditEventKind.ParentAddedChildPin,
            (recipient, ctx) => _email.SendChildBirthdayPinEligibleAsync(
                recipient, ctx.ChildName, correlationId, ct),
            (ctx) => (
                ar: $"{ctx.ChildName} أصبح بإمكانه استخدام رقم PIN.",
                en: $"{ctx.ChildName} can now use a PIN."),
            correlationId, ct);

    public Task NotifyBirthdayPasswordEligibleAsync(User child, string correlationId, CancellationToken ct = default)
        => FanOutAsync(child, KindChildBirthdayPasswordEligible, CredentialAuditEventKind.ParentUpgradedChildToPassword,
            (recipient, ctx) => _email.SendChildBirthdayPasswordEligibleAsync(
                recipient, ctx.ChildName, correlationId, ct),
            (ctx) => (
                ar: $"{ctx.ChildName} أصبح جاهزًا لكلمة مرور خاصة.",
                en: $"{ctx.ChildName} is ready for their own password."),
            correlationId, ct);

    // ── Shared pipeline ─────────────────────────────────────────────

    private sealed record EventContext(string ChildName, string ChildGrade, string ChildUsername);

    private async Task FanOutAsync(
        User child,
        string inboxKind,
        CredentialAuditEventKind auditEventKindForRecipientFilter,
        Func<IdentityNotificationRecipient, EventContext, Task> sendEmailAsync,
        Func<EventContext, (string ar, string en)> buildBodies,
        string correlationId,
        CancellationToken ct)
    {
        // 1) Resolve recipients (today: single parent; tomorrow: multi-guardian).
        var recipientUserIds = await _recipients.GetRecipientsAsync(child.Id, auditEventKindForRecipientFilter, ct).ConfigureAwait(false);
        if (recipientUserIds.Count == 0) return;

        // 2) Resolve once-per-event context shared by every recipient.
        var studentProfile = await _db.StudentProfiles.IgnoreQueryFilters()
            .Where(p => p.UserId == child.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var ctx = new EventContext(
            ChildName: child.FullName ?? child.Username ?? "—",
            ChildGrade: studentProfile?.Grade?.ToString() ?? "—",
            ChildUsername: child.Username ?? "—");

        // 3) Fan out to each recipient. Per-day dedup is per (parent, child, kind).
        var sinceUtc = DateTime.UtcNow.Subtract(DedupWindow);
        foreach (var parentUserId in recipientUserIds)
        {
            var parentProfile = await _db.ParentProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.UserId == parentUserId, ct).ConfigureAwait(false);
            if (parentProfile is null)
            {
                _logger.LogWarning("ParentProfile not found for user {ParentUserId} — skipping notification {Kind}", parentUserId, inboxKind);
                continue;
            }
            var parentUser = await _db.IdentityUsers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == parentUserId, ct).ConfigureAwait(false);
            if (parentUser is null) continue;

            var childIdForInbox = studentProfile?.UserId ?? child.Id; // inbox tracks ChildId via student id when present
            var (bodyAr, bodyEn) = buildBodies(ctx);

            var existing = await _inbox.FindLatestByKindAsync(
                parentProfile.TenantId, parentProfile.ParentProfileId, childIdForInbox,
                inboxKind, sinceUtc, ct).ConfigureAwait(false);

            if (existing is not null)
            {
                // Dedup: bump the existing row to "latest event time", suppress email.
                existing.CreatedAt = DateTime.UtcNow;
                existing.BodyAr = bodyAr;
                existing.BodyEn = bodyEn;
                existing.CorrelationId = correlationId;
                await _inbox.UpdateAsync(existing, ct).ConfigureAwait(false);
                continue;
            }

            // Insert fresh inbox row.
            var notification = new ParentNotification
            {
                ParentNotificationId = Guid.NewGuid(),
                TenantId = parentProfile.TenantId,
                ParentProfileId = parentProfile.ParentProfileId,
                ChildId = childIdForInbox,
                NotificationKind = inboxKind,
                Channel = "in_app",
                Language = string.Equals(parentProfile.PreferredLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar",
                BodyAr = bodyAr,
                BodyEn = bodyEn,
                DeliveryState = "dispatched",
                CorrelationId = correlationId,
                CreatedAt = DateTime.UtcNow,
                DispatchedAt = DateTime.UtcNow,
            };
            await _inbox.AddAsync(notification, ct).ConfigureAwait(false);

            // Send email — non-blocking on failure (logged, never throws).
            if (!string.IsNullOrWhiteSpace(parentUser.Email))
            {
                try
                {
                    await sendEmailAsync(new IdentityNotificationRecipient(
                        TenantId: parentUser.TenantId,
                        UserId: parentUser.Id,
                        Email: parentUser.Email!,
                        FullName: parentUser.FullName ?? parentUser.Email!,
                        Locale: parentProfile.PreferredLanguage), ctx).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to send credential email kind={Kind} parent={ParentUserId} child={ChildId}",
                        inboxKind, parentUserId, child.Id);
                }
            }
        }

        // SaveChanges is the caller's responsibility — but do it here too in case
        // the caller is the daily job (no surrounding unit-of-work).
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
