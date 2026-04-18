using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Parents.OperatorImpersonation;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.ParentPreferences;

/// <summary>
/// T132 (US7) — <c>PUT /parent/notifications/preferences</c>.
///
/// Persists the parent's language, locale, timezone, channel toggles,
/// quiet-hours window, and per-child category overrides onto
/// <see cref="ParentProfile"/>.
///
/// Invariants enforced here (in addition to the dispatcher):
///   - per-child overrides may only reference a child that the authenticated
///     parent holds an active <see cref="ChildLink"/> for — a foreign child id
///     drops back to 404 so nothing leaks cross-family;
///   - an impersonated write is audited on the <c>preferences</c> surface.
///
/// The request body mirrors the shape pinned by
/// <c>specs/006-engagement-progress-parent/contracts/parent-notifications-contract.md</c>.
/// </summary>
public static class ParentPreferencesEndpoint
{
    public const string Route = "/api/parent/notifications/preferences";

    public static IEndpointRouteBuilder MapParentPreferences(this IEndpointRouteBuilder routes)
    {
        routes.MapPut(Route, HandleAsync)
            .WithName("ParentNotificationPreferences")
            .WithTags("ParentNotifications");
        routes.MapGet(Route, HandleGetAsync)
            .WithName("ParentNotificationPreferencesRead")
            .WithTags("ParentNotifications");
        return routes;
    }

    public static async Task<IResult> HandleGetAsync(
        HttpContext http,
        IParentProfileRepository profiles,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var profile = await profiles.GetAsync(tenantId, parentProfileId, ct);
        if (profile is null) return Results.NotFound();

        return Results.Ok(ParentPreferencesPayload.Project(profile));
    }

    public static async Task<IResult> HandleAsync(
        ParentPreferencesRequest body,
        HttpContext http,
        MuallimiDbContext db,
        IChildLinkRepository links,
        IOperatorImpersonationAuditor auditor,
        CancellationToken ct)
    {
        if (!ParentDashboardHeaders.TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();
        if (!ParentDashboardHeaders.TryGetParentProfileId(http, out var parentProfileId))
            return Results.Unauthorized();

        var correlationId = ParentDashboardHeaders.ResolveCorrelationId(http);
        var isImpersonation = ParentDashboardHeaders.TryGetOperatorContext(http, out var operatorActorId, out var reason);

        var profile = await db.ParentProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ParentProfileId == parentProfileId, ct);
        if (profile is null) return Results.NotFound();

        if (body.PreferredLanguage is not null
            && body.PreferredLanguage is not "ar" and not "en")
        {
            return Results.BadRequest(new { error = "preferred_language must be 'ar' or 'en'" });
        }

        if (body.PerChildOverrides is { Count: > 0 })
        {
            var activeChildren = (await links.ListActiveForParentAsync(tenantId, parentProfileId, ct))
                .Select(l => l.StudentId)
                .ToHashSet();
            var unknown = body.PerChildOverrides
                .Select(o => o.ChildId)
                .Where(id => !activeChildren.Contains(id))
                .ToArray();
            if (unknown.Length > 0)
            {
                // 404 — not 403 — so cross-family existence cannot be probed.
                return Results.NotFound();
            }
        }

        ApplyToProfile(profile, body);
        await db.SaveChangesAsync(ct);

        if (isImpersonation)
        {
            await auditor.RecordViewAsync(
                tenantId: tenantId,
                operatorActorId: operatorActorId,
                targetParentProfileId: parentProfileId,
                targetChildId: null,
                surface: OperatorImpersonationSurfaces.Preferences,
                reason: string.IsNullOrWhiteSpace(reason) ? "preferences_update" : reason,
                correlationId: correlationId,
                ct: ct);
        }

        http.Response.Headers["X-Correlation-Id"] = correlationId;
        return Results.Ok(ParentPreferencesPayload.Project(profile));
    }

    private static void ApplyToProfile(ParentProfile profile, ParentPreferencesRequest body)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        if (!string.IsNullOrWhiteSpace(body.PreferredLanguage)) profile.PreferredLanguage = body.PreferredLanguage!;
        if (!string.IsNullOrWhiteSpace(body.Locale)) profile.Locale = body.Locale!;
        if (!string.IsNullOrWhiteSpace(body.Timezone)) profile.Timezone = body.Timezone!;

        if (body.NotificationChannels is not null)
        {
            var channels = new Dictionary<string, bool>
            {
                ["in_app"] = body.NotificationChannels.InApp,
                ["email"] = body.NotificationChannels.Email,
                ["push"] = body.NotificationChannels.Push,
            };
            profile.NotificationChannels = JsonSerializer.Serialize(channels, options);
        }

        if (body.QuietHours is not null)
        {
            if (string.IsNullOrEmpty(body.QuietHours.StartTime) || string.IsNullOrEmpty(body.QuietHours.EndTime))
            {
                profile.QuietHours = "{}";
            }
            else
            {
                profile.QuietHours = JsonSerializer.Serialize(new
                {
                    start_time = body.QuietHours.StartTime,
                    end_time = body.QuietHours.EndTime,
                });
            }
        }

        if (body.PerChildOverrides is not null)
        {
            var overrides = new Dictionary<string, Dictionary<string, bool>>();
            foreach (var entry in body.PerChildOverrides)
            {
                overrides[entry.ChildId.ToString("D")] = new Dictionary<string, bool>
                {
                    ["weekly_report_ready"] = entry.NotificationCategories.WeeklyReportReady,
                    ["mastery_milestone"] = entry.NotificationCategories.MasteryMilestone,
                    ["focus_area_critical"] = entry.NotificationCategories.FocusAreaCritical,
                    ["at_risk_flagged"] = entry.NotificationCategories.AtRiskFlagged,
                    ["weekly_window_inactive"] = entry.NotificationCategories.WeeklyWindowInactive,
                };
            }
            profile.PerChildOverrides = JsonSerializer.Serialize(overrides, options);
        }

        profile.UpdatedAt = DateTime.UtcNow;
    }
}

public sealed class ParentPreferencesRequest
{
    [JsonPropertyName("preferred_language")] public string? PreferredLanguage { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("timezone")] public string? Timezone { get; set; }
    [JsonPropertyName("notification_channels")] public NotificationChannelsInput? NotificationChannels { get; set; }
    [JsonPropertyName("quiet_hours")] public QuietHoursInput? QuietHours { get; set; }
    [JsonPropertyName("per_child_overrides")] public List<PerChildOverrideInput>? PerChildOverrides { get; set; }
}

public sealed class NotificationChannelsInput
{
    [JsonPropertyName("in_app")] public bool InApp { get; set; } = true;
    [JsonPropertyName("email")] public bool Email { get; set; } = true;
    [JsonPropertyName("push")] public bool Push { get; set; } = true;
}

public sealed class QuietHoursInput
{
    [JsonPropertyName("start_time")] public string? StartTime { get; set; }
    [JsonPropertyName("end_time")] public string? EndTime { get; set; }
}

public sealed class PerChildOverrideInput
{
    [JsonPropertyName("child_id")] public Guid ChildId { get; set; }
    [JsonPropertyName("notification_categories")] public NotificationCategoriesInput NotificationCategories { get; set; } = new();
}

public sealed class NotificationCategoriesInput
{
    [JsonPropertyName("weekly_report_ready")] public bool WeeklyReportReady { get; set; } = true;
    [JsonPropertyName("mastery_milestone")] public bool MasteryMilestone { get; set; } = true;
    [JsonPropertyName("focus_area_critical")] public bool FocusAreaCritical { get; set; } = true;
    [JsonPropertyName("at_risk_flagged")] public bool AtRiskFlagged { get; set; } = true;
    [JsonPropertyName("weekly_window_inactive")] public bool WeeklyWindowInactive { get; set; } = true;
}

public static class ParentPreferencesPayload
{
    public static object Project(ParentProfile profile)
    {
        return new
        {
            preferred_language = profile.PreferredLanguage,
            locale = profile.Locale,
            timezone = profile.Timezone,
            notification_channels = ParseJson(profile.NotificationChannels) ?? new { in_app = true, email = true, push = true } as object,
            quiet_hours = ParseJson(profile.QuietHours) ?? new { } as object,
            per_child_overrides = ParseJson(profile.PerChildOverrides) ?? new { } as object,
        };
    }

    private static object? ParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
