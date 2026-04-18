using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolReports.ReportExport;

namespace Muallimi.Api.SchoolReports.ReportAggregation;

/// <summary>
/// T175 (US9) — SchoolReportGenerationJob background service.
///
/// Polls <see cref="ISchoolReportRepository.ListPendingAsync"/> and drives
/// each pending row through aggregation → export → status transition →
/// <c>report_generated</c> outbox event. Background mode is disabled by
/// default so integration tests drive <see cref="RunOnceAsync"/> directly;
/// production enables via configuration.
/// </summary>
public sealed class SchoolReportGenerationJobOptions
{
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
    public int BatchSize { get; init; } = 10;
}

public sealed class SchoolReportGenerationJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SchoolReportGenerationJob> _logger;
    private readonly SchoolReportGenerationJobOptions _options;

    public SchoolReportGenerationJob(
        IServiceProvider services,
        ILogger<SchoolReportGenerationJob> logger,
        IOptions<SchoolReportGenerationJobOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SchoolReportGenerationJob disabled; skipping tick loop");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "SchoolReportGenerationJob tick failed"); }

            try { await Task.Delay(_options.Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISchoolReportRepository>();
        var aggregator = scope.ServiceProvider.GetRequiredService<ISchoolReportAggregator>();
        var exporter = scope.ServiceProvider.GetRequiredService<ISchoolReportExporter>();
        var outbox = scope.ServiceProvider.GetRequiredService<IPhase5DownstreamEventOutbox>();

        var pending = await repo.ListPendingAsync(_options.BatchSize, ct);
        var completed = 0;
        foreach (var report in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await GenerateAsync(report, aggregator, exporter, outbox, ct);
                await repo.SaveChangesAsync(ct);
                completed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SchoolReportGenerationJob failed for report={SchoolReportId}",
                    report.SchoolReportId);
                report.Status = "failed";
                report.CompletedAt = DateTime.UtcNow;
                await repo.SaveChangesAsync(ct);
            }
        }
        return completed;
    }

    public static async Task GenerateAsync(
        Muallimi.Domain.SchoolManagement.SchoolReport report,
        ISchoolReportAggregator aggregator,
        ISchoolReportExporter exporter,
        IPhase5DownstreamEventOutbox outbox,
        CancellationToken ct = default)
    {
        var payload = await aggregator.AggregateAsync(report, ct);
        var artifact = await exporter.ExportAsync(payload, ct);
        report.ExportBlobKey = artifact.BlobKey;
        report.Status = "ready";
        report.CompletedAt = DateTime.UtcNow;

        await outbox.EnqueueAsync(
            Phase5DownstreamEventKind.report_generated,
            report.TenantId,
            report.SchoolTenantId,
            new
            {
                school_report_id = report.SchoolReportId,
                report_type = report.ReportType,
                language = report.Language,
                generated_by_admin_id = report.GeneratedByAdminId,
                window_start = report.WindowStart,
                window_end = report.WindowEnd,
                export_blob_key = report.ExportBlobKey,
            },
            correlationId: Guid.NewGuid().ToString("D"),
            occurredAt: report.CompletedAt,
            ct: ct);
    }
}

public static class SchoolReportGenerationJobServiceCollectionExtensions
{
    public static IServiceCollection AddPhase5SchoolReportGenerationJob(this IServiceCollection services)
    {
        services.AddSingleton<IHostedService, SchoolReportGenerationJob>();
        return services;
    }
}
