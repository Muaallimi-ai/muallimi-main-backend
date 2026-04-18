using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Api.Announcements.AnnouncementCreation;

namespace Muallimi.Api.Announcements.AnnouncementDispatch;

/// <summary>
/// T160 (US8) — AnnouncementScheduler background service.
///
/// Polls the scheduled-announcement queue on a fixed cadence and calls
/// <see cref="IAnnouncementDispatcher.PublishAsync"/> for every row whose
/// <c>scheduled_publish_at</c> has passed. Background mode is disabled by
/// default so integration tests + the local smoke script drive
/// <see cref="RunOnceAsync"/> directly, mirroring the Phase 4
/// FocusAreaRefreshJob pattern.
/// </summary>
public sealed class AnnouncementSchedulerOptions
{
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed class AnnouncementScheduler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AnnouncementScheduler> _logger;
    private readonly AnnouncementSchedulerOptions _options;

    public AnnouncementScheduler(
        IServiceProvider services,
        ILogger<AnnouncementScheduler> logger,
        IOptions<AnnouncementSchedulerOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AnnouncementScheduler disabled; skipping tick loop");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AnnouncementScheduler tick failed"); }

            try { await Task.Delay(_options.Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAnnouncementRepository>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IAnnouncementDispatcher>();

        var due = await repo.ListScheduledDueAsync(DateTime.UtcNow, ct);
        var published = 0;
        foreach (var row in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await dispatcher.PublishAsync(row, ct);
                published++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AnnouncementScheduler failed for announcement={AnnouncementId}",
                    row.AnnouncementId);
            }
        }
        return published;
    }
}

public static class AnnouncementSchedulerServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5AnnouncementScheduler(this IServiceCollection services)
    {
        services.AddSingleton<IHostedService, AnnouncementScheduler>();
        return services;
    }
}
