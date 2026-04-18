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

namespace Muallimi.Api.Engagement.FocusAreas;

/// <summary>
/// T111 (US5) — FocusAreaRefreshJob.
///
/// Background service that wakes on the documented cadence, enumerates
/// every active student (via <c>ChildLink.EffectiveStart/End</c>), and
/// invokes <see cref="IFocusAreaCalculator"/> for each. Each student is
/// processed inside its own scope — a single failing student never blocks
/// the remaining family tenants.
///
/// The job is disabled by default. Integration tests + the Phase 4 local
/// smoke script drive the calculator directly via <see cref="RunOnceAsync"/>.
/// </summary>
public sealed class FocusAreaRefreshJobOptions
{
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);
}

public sealed class FocusAreaRefreshJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<FocusAreaRefreshJob> _logger;
    private readonly FocusAreaRefreshJobOptions _options;

    public FocusAreaRefreshJob(
        IServiceProvider services,
        ILogger<FocusAreaRefreshJob> logger,
        IOptions<FocusAreaRefreshJobOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("FocusAreaRefreshJob disabled; skipping tick loop");
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
                _logger.LogError(ex, "FocusAreaRefreshJob tick failed");
            }

            try { await Task.Delay(_options.Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var calculator = scope.ServiceProvider.GetRequiredService<IFocusAreaCalculator>();

        var today = DateTime.UtcNow.Date;
        var activeLinks = await db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.EffectiveStart <= today && (l.EffectiveEnd == null || l.EffectiveEnd >= today))
            .Select(l => new { l.TenantId, l.StudentId })
            .Distinct()
            .ToListAsync(ct);

        foreach (var link in activeLinks)
        {
            ct.ThrowIfCancellationRequested();
            var correlationId = Guid.NewGuid().ToString("D");
            try
            {
                await calculator.RecomputeAsync(link.TenantId, link.StudentId, correlationId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FocusArea refresh failed for tenant={Tenant} student={Student}",
                    link.TenantId, link.StudentId);
            }
        }
    }
}

public static class FocusAreaRefreshJobServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4FocusAreaRefreshJob(this IServiceCollection services)
    {
        services.AddSingleton<IHostedService, FocusAreaRefreshJob>();
        return services;
    }
}
