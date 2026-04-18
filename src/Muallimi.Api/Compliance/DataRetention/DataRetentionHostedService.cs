using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Muallimi.Api.Compliance.DataRetention;

/// <summary>
/// T117 — Background worker that invokes <see cref="DataRetentionService"/> on
/// the configured daily schedule. The interval is kept short in Development so
/// smoke scripts can observe a run within a single walkthrough.
/// </summary>
public sealed class DataRetentionHostedService : BackgroundService
{
    private static readonly Guid SystemActorId = new("00000000-0000-0000-0000-00000000c8a8");

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DataRetentionHostedService> _logger;
    private readonly TimeSpan _interval;

    public DataRetentionHostedService(
        IServiceScopeFactory scopes,
        ILogger<DataRetentionHostedService> logger,
        IHostEnvironment environment)
    {
        _scopes = scopes;
        _logger = logger;
        _interval = environment.IsDevelopment() ? TimeSpan.FromMinutes(60) : TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so DI container and migrations settle before the first run.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<DataRetentionService>();
                var result = await service.ExecuteAsync(
                    SystemActorId, Guid.NewGuid().ToString(), stoppingToken);
                _logger.LogInformation(
                    "data_retention.run policies={Policies} rows={Rows} durationSec={Duration}",
                    result.PoliciesEvaluated, result.RowsAffected, result.DurationSeconds);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "data_retention.run_failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
