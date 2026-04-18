using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Muallimi.Api.Leaderboards.LeaderboardComputation;
using Muallimi.Api.SchoolManagement;
using Muallimi.Domain.SchoolManagement;

namespace Muallimi.Api.Leaderboards.LeaderboardQuery;

/// <summary>
/// T147 (US7) — school admin leaderboard config endpoints.
///
/// Routes:
///   • GET /api/school-admin/leaderboards/config
///   • PUT /api/school-admin/leaderboards/config
///
/// School admins are the only principals allowed to read or modify the
/// config. Invalid privacy modes are rejected with 400 so the ranking
/// pipeline never has to defend against unexpected enums at snapshot
/// time. The per-class overrides array is validated as JSON but the
/// semantic meaning (class ownership) is enforced implicitly by the
/// school-scoped query in <see cref="LeaderboardComputationService"/>.
/// </summary>
public static class LeaderboardConfigEndpoints
{
    public const string GetRoute = "/api/school-admin/leaderboards/config";
    public const string PutRoute = "/api/school-admin/leaderboards/config";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public sealed record PerClassOverridePayload(Guid class_group_id, bool enabled);

    public sealed record ConfigRequest(
        string privacy_mode,
        bool leaderboard_enabled,
        List<PerClassOverridePayload>? per_class_overrides);

    public static IEndpointRouteBuilder MapLeaderboardConfig(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(GetRoute, HandleGetAsync).WithName("GetLeaderboardConfig").WithTags("Leaderboards");
        routes.MapPut(PutRoute, HandlePutAsync).WithName("PutLeaderboardConfig").WithTags("Leaderboards");
        return routes;
    }

    public static async Task<IResult> HandleGetAsync(
        HttpContext http,
        ILeaderboardConfigRepository repo,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetSchoolAdminId(http, out _))
        {
            return Results.Unauthorized();
        }

        var config = await repo.GetOrDefaultAsync(tenantId, schoolTenantId, ct);
        return Results.Ok(Project(config));
    }

    public static async Task<IResult> HandlePutAsync(
        HttpContext http,
        ConfigRequest body,
        ILeaderboardConfigRepository repo,
        CancellationToken ct)
    {
        if (!SchoolManagementHeaders.TryGetTenantId(http, out var tenantId)
            || !SchoolManagementHeaders.TryGetSchoolTenantId(http, out var schoolTenantId)
            || !SchoolManagementHeaders.TryGetSchoolAdminId(http, out _))
        {
            return Results.Unauthorized();
        }

        if (body is null || string.IsNullOrWhiteSpace(body.privacy_mode)
            || !PrivacyModes.IsValid(body.privacy_mode))
        {
            return Results.BadRequest(new { error = "invalid_privacy_mode" });
        }

        var overrides = body.per_class_overrides ?? new List<PerClassOverridePayload>();
        var row = new LeaderboardConfig
        {
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            PrivacyMode = body.privacy_mode,
            LeaderboardEnabled = body.leaderboard_enabled,
            PerClassOverridesJson = JsonSerializer.Serialize(overrides, JsonOptions),
        };

        await repo.UpsertAsync(row, ct);
        await repo.SaveChangesAsync(ct);

        var saved = await repo.GetOrDefaultAsync(tenantId, schoolTenantId, ct);
        return Results.Ok(Project(saved));
    }

    private static object Project(LeaderboardConfig config)
    {
        List<PerClassOverridePayload> overrides;
        try
        {
            overrides = JsonSerializer.Deserialize<List<PerClassOverridePayload>>(
                config.PerClassOverridesJson, JsonOptions)
                ?? new List<PerClassOverridePayload>();
        }
        catch
        {
            overrides = new List<PerClassOverridePayload>();
        }

        return new
        {
            privacy_mode = config.PrivacyMode,
            leaderboard_enabled = config.LeaderboardEnabled,
            per_class_overrides = overrides,
            updated_at = config.UpdatedAt,
        };
    }
}
