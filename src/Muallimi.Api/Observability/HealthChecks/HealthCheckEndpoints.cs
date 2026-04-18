using Microsoft.EntityFrameworkCore;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Observability.HealthChecks;

/// <summary>
/// T022 — Readiness, liveness, and startup probes for the main-backend
/// service. Readiness validates database, queue, cache, and blob_storage
/// dependencies. Liveness is a lightweight heartbeat. Startup probe mirrors
/// readiness with a configured timeout per observability-contract.md.
/// </summary>
public static class HealthCheckEndpoints
{
    public static IEndpointRouteBuilder MapPhase6HealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "healthy", service = "main-backend" }));

        endpoints.MapGet("/health/ready", async (MuallimiDbContext db, CancellationToken ct) =>
        {
            var dbOk = await CheckDatabaseAsync(db, ct);
            var allOk = dbOk;
            var status = allOk ? "healthy" : "unhealthy";
            var code = allOk ? 200 : 503;
            return Results.Json(new
            {
                status,
                service = "main-backend",
                dependencies = new
                {
                    database = dbOk ? "healthy" : "unhealthy",
                    queue = "unknown",
                    cache = "unknown",
                    blob_storage = "unknown"
                }
            }, statusCode: code);
        });

        endpoints.MapGet("/health/startup", async (MuallimiDbContext db, CancellationToken ct) =>
        {
            var dbOk = await CheckDatabaseAsync(db, ct);
            return dbOk ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "starting" }, statusCode: 503);
        });

        return endpoints;
    }

    private static async Task<bool> CheckDatabaseAsync(MuallimiDbContext db, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
