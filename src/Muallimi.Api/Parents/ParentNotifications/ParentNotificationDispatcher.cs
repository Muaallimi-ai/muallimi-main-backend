using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.ParentNotifications;

/// <summary>
/// T129 (US7) — Parent notification dispatcher.
///
/// Writes a <see cref="ParentNotification"/> row inside the caller's unit
/// of work, resolves the active parent preferences at send time, and:
///
///   - refuses to dispatch when every channel is disabled or the category
///     is turned off for the specific child (the row is written with
///     <c>delivery_state = suppressed</c> so the dashboard can still show
///     the underlying state — the contract forbids dropping silently);
///   - defers dispatch when the current moment is inside the parent's
///     quiet-hours window (sets <c>quiet_hours_deferred_until</c> to the
///     next end-of-window moment and leaves <c>delivery_state = deferred</c>);
///     deferred notifications are NEVER dropped — the
///     <c>FlushDeferredAsync</c> entry point re-drives them once the quiet
///     window closes;
///   - otherwise picks the highest-priority channel the parent has
///     enabled (<c>push &gt; email &gt; in_app</c> for urgent kinds,
///     <c>in_app &gt; email &gt; push</c> otherwise), resolves the
///     language at send time, and invokes the matching
///     <see cref="INotificationChannelAdapter"/>.
///
/// Language resolution uses the parent's preferred language at the
/// dispatch moment so toggling the preference applies to subsequent
/// notifications without regenerating past reports (contract invariant).
/// </summary>
public interface IParentNotificationDispatcher
{
    Task<ParentNotificationDispatchOutcome> EnqueueAsync(
        ParentNotificationDispatchInput input,
        CancellationToken ct = default);

    Task FlushDeferredAsync(CancellationToken ct = default);
}

public sealed record ParentNotificationDispatchInput(
    Guid TenantId,
    Guid ParentProfileId,
    Guid ChildId,
    string NotificationKind,
    string? BodyAr,
    string? BodyEn,
    string? DeepLink,
    string CorrelationId);

public enum ParentNotificationDispatchStatus
{
    Dispatched,
    Deferred,
    Suppressed,
    Failed,
}

public sealed record ParentNotificationDispatchOutcome(
    Guid ParentNotificationId,
    ParentNotificationDispatchStatus Status,
    string? Channel,
    string? Language,
    DateTime? DeferredUntil);

public sealed class ParentNotificationDispatcher : IParentNotificationDispatcher
{
    private static readonly IReadOnlyList<string> UrgentFirst = new[] { "push", "email", "in_app" };
    private static readonly IReadOnlyList<string> RoutineFirst = new[] { "in_app", "email", "push" };
    private static readonly HashSet<string> UrgentKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "at_risk_flagged",
        "focus_area_critical",
    };

    private readonly MuallimiDbContext _db;
    private readonly IParentNotificationRepository _notifications;
    private readonly IParentProfileRepository _profiles;
    private readonly INotificationChannelAdapterRegistry _channels;
    private readonly ILogger<ParentNotificationDispatcher> _logger;

    public ParentNotificationDispatcher(
        MuallimiDbContext db,
        IParentNotificationRepository notifications,
        IParentProfileRepository profiles,
        INotificationChannelAdapterRegistry channels,
        ILogger<ParentNotificationDispatcher> logger)
    {
        _db = db;
        _notifications = notifications;
        _profiles = profiles;
        _channels = channels;
        _logger = logger;
    }

    public async Task<ParentNotificationDispatchOutcome> EnqueueAsync(
        ParentNotificationDispatchInput input,
        CancellationToken ct = default)
    {
        var profile = await _profiles.GetAsync(input.TenantId, input.ParentProfileId, ct)
            ?? throw new InvalidOperationException($"ParentProfile {input.ParentProfileId} not found in tenant {input.TenantId}");

        var language = string.Equals(profile.PreferredLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        var preferences = ParentNotificationPreferencesReader.Read(profile);

        var notification = new ParentNotification
        {
            ParentNotificationId = Guid.NewGuid(),
            TenantId = input.TenantId,
            ParentProfileId = input.ParentProfileId,
            ChildId = input.ChildId,
            NotificationKind = input.NotificationKind,
            Channel = "in_app",
            Language = language,
            BodyAr = input.BodyAr,
            BodyEn = input.BodyEn,
            DeliveryState = "queued",
            CorrelationId = input.CorrelationId,
            CreatedAt = DateTime.UtcNow,
        };

        // Category suppression — per-child overrides take precedence over the
        // tenant-wide default. A disabled category writes the row anyway so
        // the underlying state stays visible on the dashboard (contract
        // invariant: disabled categories MUST NOT hide state).
        if (!preferences.IsCategoryEnabled(input.ChildId, input.NotificationKind))
        {
            notification.DeliveryState = "suppressed";
            await _notifications.AddAsync(notification, ct);
            return new ParentNotificationDispatchOutcome(
                notification.ParentNotificationId,
                ParentNotificationDispatchStatus.Suppressed,
                Channel: null,
                Language: language,
                DeferredUntil: null);
        }

        var channel = preferences.PickChannel(
            UrgentKinds.Contains(input.NotificationKind) ? UrgentFirst : RoutineFirst);
        if (channel is null)
        {
            notification.DeliveryState = "suppressed";
            await _notifications.AddAsync(notification, ct);
            return new ParentNotificationDispatchOutcome(
                notification.ParentNotificationId,
                ParentNotificationDispatchStatus.Suppressed,
                Channel: null,
                Language: language,
                DeferredUntil: null);
        }
        notification.Channel = channel;

        var deferUntil = preferences.ResolveQuietHoursDeferral(profile.Timezone, DateTime.UtcNow);
        if (deferUntil is not null)
        {
            notification.DeliveryState = "deferred";
            notification.QuietHoursDeferredUntil = deferUntil;
            await _notifications.AddAsync(notification, ct);
            return new ParentNotificationDispatchOutcome(
                notification.ParentNotificationId,
                ParentNotificationDispatchStatus.Deferred,
                Channel: channel,
                Language: language,
                DeferredUntil: deferUntil);
        }

        await _notifications.AddAsync(notification, ct);
        await DispatchAsync(notification, input.DeepLink, ct);
        return new ParentNotificationDispatchOutcome(
            notification.ParentNotificationId,
            notification.DeliveryState == "failed"
                ? ParentNotificationDispatchStatus.Failed
                : ParentNotificationDispatchStatus.Dispatched,
            Channel: channel,
            Language: language,
            DeferredUntil: null);
    }

    public async Task FlushDeferredAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var ready = await _notifications.ListDeferredReadyForDispatchAsync(now, ct);
        foreach (var notification in ready)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DispatchAsync(notification, deepLink: null, ct);
                await _notifications.UpdateAsync(notification, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FlushDeferred failed for parent_notification_id={Id}", notification.ParentNotificationId);
                notification.DeliveryState = "failed";
                await _notifications.UpdateAsync(notification, ct);
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task DispatchAsync(ParentNotification notification, string? deepLink, CancellationToken ct)
    {
        var adapter = _channels.Get(notification.Channel);
        var body = string.Equals(notification.Language, "en", StringComparison.OrdinalIgnoreCase)
            ? (notification.BodyEn ?? notification.BodyAr ?? string.Empty)
            : (notification.BodyAr ?? notification.BodyEn ?? string.Empty);

        var metadata = new Dictionary<string, string>
        {
            ["parent_notification_id"] = notification.ParentNotificationId.ToString("D"),
            ["notification_kind"] = notification.NotificationKind,
            ["deep_link"] = deepLink ?? string.Empty,
        };
        var request = new NotificationDispatchRequest(
            TenantId: notification.TenantId,
            ParentProfileId: notification.ParentProfileId,
            ChildId: notification.ChildId,
            NotificationKind: notification.NotificationKind,
            Language: notification.Language,
            Title: notification.NotificationKind,
            Body: body,
            Metadata: metadata,
            CorrelationId: notification.CorrelationId);
        try
        {
            var receipt = await adapter.DispatchAsync(request, ct);
            notification.DeliveryState = "dispatched";
            notification.DispatchedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "Dispatched parent_notification_id={Id} channel={Channel} receipt_id={ReceiptId}",
                notification.ParentNotificationId, notification.Channel, receipt.ReceiptId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dispatch failed for parent_notification_id={Id} channel={Channel}",
                notification.ParentNotificationId, notification.Channel);
            notification.DeliveryState = "failed";
        }
    }
}

/// <summary>
/// Typed projection of the JSON columns on <see cref="ParentProfile"/>.
/// Lives next to the dispatcher so every call site reads preferences the
/// same way.
/// </summary>
public sealed class ParentNotificationPreferences
{
    public IReadOnlyDictionary<string, bool> Channels { get; init; } = new Dictionary<string, bool>();
    public TimeSpan? QuietHoursStart { get; init; }
    public TimeSpan? QuietHoursEnd { get; init; }
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, bool>> PerChildCategoryOverrides { get; init; }
        = new Dictionary<Guid, IReadOnlyDictionary<string, bool>>();

    public bool IsCategoryEnabled(Guid childId, string notificationKind)
    {
        if (PerChildCategoryOverrides.TryGetValue(childId, out var overrides)
            && overrides.TryGetValue(notificationKind, out var enabled))
        {
            return enabled;
        }
        return true;
    }

    public string? PickChannel(IReadOnlyList<string> priorityOrder)
    {
        foreach (var candidate in priorityOrder)
        {
            if (Channels.TryGetValue(candidate, out var enabled) && enabled)
            {
                return candidate;
            }
        }
        return null;
    }

    public DateTime? ResolveQuietHoursDeferral(string timezoneId, DateTime utcNow)
    {
        if (QuietHoursStart is null || QuietHoursEnd is null) return null;
        if (QuietHoursStart == QuietHoursEnd) return null;

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(timezoneId); }
        catch { tz = TimeZoneInfo.Utc; }

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
        var localDate = localNow.Date;
        var localTime = localNow.TimeOfDay;
        var start = QuietHoursStart.Value;
        var end = QuietHoursEnd.Value;

        bool inQuiet;
        DateTime nextEndLocal;
        if (start < end)
        {
            inQuiet = localTime >= start && localTime < end;
            nextEndLocal = localDate + end;
        }
        else
        {
            inQuiet = localTime >= start || localTime < end;
            nextEndLocal = localTime >= start ? localDate.AddDays(1) + end : localDate + end;
        }

        if (!inQuiet) return null;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextEndLocal, DateTimeKind.Unspecified), tz);
    }
}

public static class ParentNotificationPreferencesReader
{
    public static ParentNotificationPreferences Read(ParentProfile profile)
    {
        var channels = ParseChannels(profile.NotificationChannels);
        var (start, end) = ParseQuietHours(profile.QuietHours);
        var overrides = ParsePerChildOverrides(profile.PerChildOverrides);
        return new ParentNotificationPreferences
        {
            Channels = channels,
            QuietHoursStart = start,
            QuietHoursEnd = end,
            PerChildCategoryOverrides = overrides,
        };
    }

    private static IReadOnlyDictionary<string, bool> ParseChannels(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return DefaultChannels();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind == JsonValueKind.True;
            }
            if (map.Count == 0) return DefaultChannels();
            return map;
        }
        catch (JsonException)
        {
            return DefaultChannels();
        }
    }

    private static (TimeSpan? start, TimeSpan? end) ParseQuietHours(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("start_time", out var startElem)) return (null, null);
            if (!doc.RootElement.TryGetProperty("end_time", out var endElem)) return (null, null);
            if (!TimeSpan.TryParse(startElem.GetString(), out var start)) return (null, null);
            if (!TimeSpan.TryParse(endElem.GetString(), out var end)) return (null, null);
            return (start, end);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, bool>> ParsePerChildOverrides(string json)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, bool>>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!Guid.TryParse(property.Name, out var childId)) continue;
                var perKind = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var kindProp in property.Value.EnumerateObject())
                {
                    perKind[kindProp.Name] = kindProp.Value.ValueKind == JsonValueKind.True;
                }
                result[childId] = perKind;
            }
        }
        catch (JsonException)
        {
            // Malformed override JSON falls back to defaults-on for every category.
        }
        return result;
    }

    private static IReadOnlyDictionary<string, bool> DefaultChannels()
        => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["in_app"] = true,
            ["email"] = true,
            ["push"] = true,
        };
}

public static class ParentNotificationDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4ParentNotificationDispatcher(this IServiceCollection services)
    {
        services.AddScoped<IParentNotificationDispatcher, ParentNotificationDispatcher>();
        return services;
    }
}
