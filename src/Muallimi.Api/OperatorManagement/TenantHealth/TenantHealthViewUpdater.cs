using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.OperatorManagement.TenantHealth;

/// <summary>
/// T026 — Periodic aggregation of tenant health. Full rollup (subscription,
/// active students, session counts, AI cost, storage, engagement, at-risk)
/// lands in US6. Foundational scaffolding runs every 15 minutes and records
/// the ComputedAt timestamp so downstream queries have a freshness signal.
/// </summary>
public class TenantHealthViewUpdater : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TenantHealthViewUpdater> _logger;

    public TenantHealthViewUpdater(
        IServiceProvider serviceProvider,
        ILogger<TenantHealthViewUpdater> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
                await RefreshAsync(db, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TenantHealthViewUpdater refresh failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    internal static async Task RefreshAsync(MuallimiDbContext db, CancellationToken ct)
    {
        var rollup = new TenantHealthRollupService(db);
        await rollup.RefreshAllAsync(ct);
    }
}
