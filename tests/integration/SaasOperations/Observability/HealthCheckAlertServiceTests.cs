using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Muallimi.Api.Observability.HealthChecks;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.Observability;

/// <summary>
/// T082 — Verifies state-change detection in the health alert service.
/// </summary>
public class HealthCheckAlertServiceTests
{
    private sealed class StubSink : IHealthAlertSink
    {
        public List<HealthAlert> Fired { get; } = new();
        public List<string> Resolved { get; } = new();
        public Task FireAsync(HealthAlert a, CancellationToken ct = default) { Fired.Add(a); return Task.CompletedTask; }
        public Task ResolveAsync(string service, CancellationToken ct = default) { Resolved.Add(service); return Task.CompletedTask; }
    }

    private sealed class NoOpHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }

    [Fact]
    public async Task HandleStatusChange_fires_on_first_unhealthy_only()
    {
        var sink = new StubSink();
        var options = Options.Create(new HealthCheckAlertOptions());
        var svc = new HealthCheckAlertService(options, new NoOpHttpFactory(), sink,
            NullLogger<HealthCheckAlertService>.Instance);

        await svc.HandleStatusChangeAsync("main-backend", "unhealthy", CancellationToken.None);
        await svc.HandleStatusChangeAsync("main-backend", "unhealthy", CancellationToken.None);

        Assert.Single(sink.Fired);
        Assert.Equal("main-backend", sink.Fired[0].ServiceName);
    }

    [Fact]
    public async Task HandleStatusChange_resolves_when_returning_to_healthy()
    {
        var sink = new StubSink();
        var options = Options.Create(new HealthCheckAlertOptions());
        var svc = new HealthCheckAlertService(options, new NoOpHttpFactory(), sink,
            NullLogger<HealthCheckAlertService>.Instance);

        await svc.HandleStatusChangeAsync("ai-service", "unhealthy", CancellationToken.None);
        await svc.HandleStatusChangeAsync("ai-service", "healthy", CancellationToken.None);

        Assert.Single(sink.Fired);
        Assert.Single(sink.Resolved);
        Assert.Equal("ai-service", sink.Resolved[0]);
    }
}
