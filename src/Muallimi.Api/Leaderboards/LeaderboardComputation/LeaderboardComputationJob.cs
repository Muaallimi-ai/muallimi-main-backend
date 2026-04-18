using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Leaderboards.LeaderboardComputation;

/// <summary>
/// T145 (US7) — LeaderboardComputationJob.
///
/// Iterates every active class in every school tenant on a fixed cadence
/// and delegates snapshot computation to
/// <see cref="ILeaderboardComputationService"/>. Snapshots are materialised
/// once per tick so that query endpoints can serve pre-computed rows
/// without recomputing on demand (per the contract invariant).
///
/// Background mode is disabled by default; integration tests drive
/// <see cref="RunOnceAsync"/> directly.
/// </summary>
public sealed class LeaderboardComputationJobOptions
{
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
    public string Metric { get; init; } = LeaderboardMetrics.Mastery;
    public TimeSpan Window { get; init; } = TimeSpan.FromDays(7);
}

public sealed class LeaderboardComputationJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LeaderboardComputationJob> _logger;
    private readonly LeaderboardComputationJobOptions _options;

    public LeaderboardComputationJob(
        IServiceProvider services,
        ILogger<LeaderboardComputationJob> logger,
        IOptions<LeaderboardComputationJobOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("LeaderboardComputationJob disabled; skipping tick loop");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LeaderboardComputationJob tick failed");
            }

            try { await Task.Delay(_options.Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var compute = scope.ServiceProvider.GetRequiredService<ILeaderboardComputationService>();

        var now = DateTime.UtcNow;
        var windowStart = now - _options.Window;

        var classes = await db.ClassGroups
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new { c.TenantId, c.SchoolTenantId, c.ClassGroupId })
            .ToListAsync(ct);

        var count = 0;
        foreach (var cls in classes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await compute.ComputeClassSnapshotAsync(
                    cls.TenantId,
                    cls.SchoolTenantId,
                    cls.ClassGroupId,
                    _options.Metric,
                    subjectId: null,
                    windowStart,
                    now,
                    ct);
                if (snapshot is not null) count++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "LeaderboardComputationJob failed for tenant={Tenant} class={Class}",
                    cls.TenantId, cls.ClassGroupId);
            }
        }
        return count;
    }
}

public static class LeaderboardComputationJobServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5LeaderboardComputationJob(this IServiceCollection services)
    {
        services.AddSingleton<IHostedService, LeaderboardComputationJob>();
        return services;
    }
}
