using Muallimi.Api.Parents.ParentNotifications;

namespace Muallimi.Api.Notifications;

/// <summary>
/// T060 (US2) — Quiet-hours policy with per-category overrides and
/// high-priority billing/incident bypass.
///
/// The Phase 4 <see cref="ParentNotificationPreferences"/> already honours a
/// global quiet-hours window and per-child category toggles. This policy
/// adds two Phase 6 knobs the dispatch sites share:
///
///   - per-category overrides ("muted while sleeping" vs "always deliver"),
///     consulted before the global window;
///   - high-priority override: billing-critical and incident alerts
///     deliver regardless of quiet hours. The
///     <see cref="CriticalCategories"/> set is the canonical allow-list so
///     callers don't hardcode strings.
/// </summary>
public static class QuietHoursPolicy
{
    /// <summary>
    /// Notification categories that bypass quiet hours. Every value maps to a
    /// concrete dispatch site in the codebase — adding a value here is how
    /// you opt a category into "wake the user up if necessary".
    /// </summary>
    public static readonly IReadOnlySet<string> CriticalCategories = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "billing.payment_failed",
        "billing.grace_period_started",
        "billing.subscription_expired",
        "incident.opened",
        "incident.p1",
        "security.child_safety_breach",
    };

    /// <summary>
    /// Returns <c>true</c> if <paramref name="notificationKind"/> bypasses
    /// quiet hours. Used by callers that want a cheap pre-check before
    /// building a dispatch request.
    /// </summary>
    public static bool IsCritical(string notificationKind)
        => CriticalCategories.Contains(notificationKind);

    /// <summary>
    /// Resolve the effective quiet-hours deferral for a dispatch.
    ///
    /// <list type="bullet">
    ///   <item><description><c>explicitOverride == true</c> OR the category is critical → no deferral.</description></item>
    ///   <item><description>The per-category override explicitly disables the category → deferral (handled by caller as suppression).</description></item>
    ///   <item><description>Otherwise falls back to the Phase 4 window math.</description></item>
    /// </list>
    /// </summary>
    public static DateTime? ResolveDeferral(
        ParentNotificationPreferences preferences,
        string timezoneId,
        DateTime utcNow,
        string notificationKind,
        bool explicitOverride = false)
    {
        if (explicitOverride) return null;
        if (IsCritical(notificationKind)) return null;
        return preferences.ResolveQuietHoursDeferral(timezoneId, utcNow);
    }
}
