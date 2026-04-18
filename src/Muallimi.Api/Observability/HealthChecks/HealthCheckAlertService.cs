using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Muallimi.Api.Observability.HealthChecks;

/// <summary>
/// T082 — Monitors /health/ready for main-backend, ai-service,
/// document-ingestion, and the frontend. On unhealthy status, emits an
/// operator alert within the configured window (default 60 s).
///
/// Local-parity: invokes <see cref="IHealthAlertSink"/>, which logs in dev
/// and can be swapped for a production sink (notification dispatcher / alert
/// rule engine) via DI. Services pulled from configuration keyed by role.
/// </summary>
public sealed class HealthCheckAlertService : BackgroundService
{
    private readonly HealthCheckAlertOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHealthAlertSink _sink;
    private readonly ILogger<HealthCheckAlertService> _logger;
    private readonly Dictionary<string, string> _lastStatus = new(StringComparer.OrdinalIgnoreCase);

    public HealthCheckAlertService(
        IOptions<HealthCheckAlertOptions> options,
        IHttpClientFactory httpFactory,
        IHealthAlertSink sink,
        ILogger<HealthCheckAlertService> logger)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _sink = sink;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Services.Count == 0)
        {
            _logger.LogInformation("HealthCheckAlertService disabled — no services configured.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var target in _options.Services)
            {
                var status = await ProbeAsync(target, stoppingToken);
                await HandleStatusChangeAsync(target.Name, status, stoppingToken);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.PollIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<string> ProbeAsync(HealthTarget target, CancellationToken ct)
    {
        try
        {
            using var client = _httpFactory.CreateClient("health-alert");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(2, _options.RequestTimeoutSeconds));
            var url = target.ReadinessUrl.TrimEnd('/');
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return "unhealthy";
            var payload = await response.Content.ReadFromJsonAsync<HealthResponse>(cancellationToken: ct);
            return payload?.Status ?? "unhealthy";
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health probe failed for {Service}", target.Name);
            return "unhealthy";
        }
    }

    public async Task HandleStatusChangeAsync(string serviceName, string status, CancellationToken ct)
    {
        var prev = _lastStatus.TryGetValue(serviceName, out var p) ? p : "unknown";
        _lastStatus[serviceName] = status;

        if (status == "unhealthy" && prev != "unhealthy")
        {
            _logger.LogError("Service {Service} is UNHEALTHY — firing operator alert.", serviceName);
            await _sink.FireAsync(new HealthAlert
            {
                ServiceName = serviceName,
                Status = status,
                DetectedAt = DateTime.UtcNow,
            }, ct);
        }
        else if (status == "healthy" && prev == "unhealthy")
        {
            _logger.LogInformation("Service {Service} recovered.", serviceName);
            await _sink.ResolveAsync(serviceName, ct);
        }
    }

    private sealed record HealthResponse(string? Status);
}

public sealed class HealthCheckAlertOptions
{
    public int PollIntervalSeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 5;
    public List<HealthTarget> Services { get; set; } = new();
}

public sealed class HealthTarget
{
    public string Name { get; set; } = string.Empty;
    public string ReadinessUrl { get; set; } = string.Empty;
}

public interface IHealthAlertSink
{
    Task FireAsync(HealthAlert alert, CancellationToken ct = default);
    Task ResolveAsync(string serviceName, CancellationToken ct = default);
}

public sealed record HealthAlert
{
    public required string ServiceName { get; init; }
    public required string Status { get; init; }
    public required DateTime DetectedAt { get; init; }
}

/// <summary>
/// Default local-parity sink — logs alerts. Production swaps this for a
/// notification dispatcher binding via DI.
/// </summary>
public sealed class LoggingHealthAlertSink : IHealthAlertSink
{
    private readonly ILogger<LoggingHealthAlertSink> _logger;

    public LoggingHealthAlertSink(ILogger<LoggingHealthAlertSink> logger)
    {
        _logger = logger;
    }

    public Task FireAsync(HealthAlert alert, CancellationToken ct = default)
    {
        _logger.LogError(
            "health_alert service={Service} status={Status} detected_at={DetectedAt:o}",
            alert.ServiceName, alert.Status, alert.DetectedAt);
        return Task.CompletedTask;
    }

    public Task ResolveAsync(string serviceName, CancellationToken ct = default)
    {
        _logger.LogInformation("health_alert_resolved service={Service}", serviceName);
        return Task.CompletedTask;
    }
}
