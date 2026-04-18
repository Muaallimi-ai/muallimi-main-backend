using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T093 (US3) — Weekly report generator.
///
/// Exposes <see cref="IWeeklyReportGenerator"/> for on-demand single-child
/// generation (used by the regenerate endpoint + integration tests) and
/// <see cref="WeeklyReportGenerationJob"/>, a <see cref="BackgroundService"/>
/// that wakes up on the documented cadence and processes every active child
/// in every tenant.
///
/// The generator:
///   1. Aggregates the window via <see cref="IWeeklyReportAggregator"/>.
///   2. Creates the <see cref="WeeklyReport"/> row in <c>generating</c>
///      status inside a single unit-of-work.
///   3. Invokes <see cref="IWeeklyReportSummaryGenerator"/> which runs both
///      Arabic and English through the Phase 2 guardrail chain and writes
///      the decision trail.
///   4. Finalises the row (status = <c>ready</c>), emits the
///      <c>weekly_report_generated</c> downstream event, and commits
///      everything together.
///
/// Uniqueness: the DB UNIQUE
/// <c>(tenant_id, student_id, window_start, window_end)</c> enforces
/// one row per window. A second call for the same window is treated as
/// "idempotent no-op" if a <c>ready</c> row already exists — this keeps
/// replay safe.
/// </summary>
public interface IWeeklyReportGenerator
{
    Task<WeeklyReportGenerationResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        DateTime windowStart,
        DateTime windowEnd,
        string correlationId,
        bool forceRegenerate,
        CancellationToken ct = default);
}

public enum WeeklyReportGenerationOutcome
{
    Generated,
    Ready,
    Regenerated,
    Failed,
}

public sealed record WeeklyReportGenerationResult(
    Guid WeeklyReportId,
    WeeklyReportGenerationOutcome Outcome);

public sealed class WeeklyReportGenerator : IWeeklyReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly MuallimiDbContext _db;
    private readonly IWeeklyReportRepository _reports;
    private readonly IWeeklyReportAggregator _aggregator;
    private readonly IWeeklyReportSummaryGenerator _summaries;
    private readonly IWeeklyReportEventEmitter _events;
    private readonly INotificationSchedulerHook? _notificationHook;
    private readonly ILogger<WeeklyReportGenerator> _logger;

    public WeeklyReportGenerator(
        MuallimiDbContext db,
        IWeeklyReportRepository reports,
        IWeeklyReportAggregator aggregator,
        IWeeklyReportSummaryGenerator summaries,
        IWeeklyReportEventEmitter events,
        ILogger<WeeklyReportGenerator> logger,
        INotificationSchedulerHook? notificationHook = null)
    {
        _db = db;
        _reports = reports;
        _aggregator = aggregator;
        _summaries = summaries;
        _events = events;
        _notificationHook = notificationHook;
        _logger = logger;
    }

    public async Task<WeeklyReportGenerationResult> GenerateAsync(
        Guid tenantId,
        Guid studentId,
        DateTime windowStart,
        DateTime windowEnd,
        string correlationId,
        bool forceRegenerate,
        CancellationToken ct = default)
    {
        var start = DateTime.SpecifyKind(windowStart.Date, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(windowEnd.Date, DateTimeKind.Utc);
        if (start > end) throw new ArgumentException("windowStart must be <= windowEnd");

        var existing = await _reports.GetByWindowAsync(tenantId, studentId, start, end, ct);
        if (existing is not null && existing.Status == "ready" && !forceRegenerate)
        {
            return new WeeklyReportGenerationResult(existing.WeeklyReportId, WeeklyReportGenerationOutcome.Ready);
        }

        if (existing is not null && forceRegenerate)
        {
            existing.Status = "regenerating";
            await _reports.UpdateAsync(existing, ct);
            await _db.SaveChangesAsync(ct);
        }

        var aggregate = await _aggregator.AggregateAsync(tenantId, studentId, start, end, ct);

        var runId = Guid.NewGuid();
        var report = existing is null
            ? new WeeklyReport
            {
                WeeklyReportId = Guid.NewGuid(),
                TenantId = tenantId,
                StudentId = studentId,
                WindowStart = start,
                WindowEnd = end,
                GeneratedAt = DateTime.UtcNow,
                RunId = runId,
                MasteryDeltas = JsonSerializer.Serialize(aggregate.MasteryDeltas, JsonOptions),
                TopFocusAreas = JsonSerializer.Serialize(aggregate.TopFocusAreas, JsonOptions),
                AwardedBadges = JsonSerializer.Serialize(aggregate.AwardedBadges, JsonOptions),
                SummaryAr = string.Empty,
                SummaryEn = string.Empty,
                GuardrailDecisionTrailId = Guid.Empty,
                EvidenceRefs = JsonSerializer.Serialize(aggregate.EvidenceRefs, JsonOptions),
                ShareTokenHash = null,
                CorrelationId = correlationId,
                Status = "generating",
            }
            : existing;

        if (existing is null)
        {
            await _reports.AddAsync(report, ct);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            report.GeneratedAt = DateTime.UtcNow;
            report.RunId = runId;
            report.MasteryDeltas = JsonSerializer.Serialize(aggregate.MasteryDeltas, JsonOptions);
            report.TopFocusAreas = JsonSerializer.Serialize(aggregate.TopFocusAreas, JsonOptions);
            report.AwardedBadges = JsonSerializer.Serialize(aggregate.AwardedBadges, JsonOptions);
            report.EvidenceRefs = JsonSerializer.Serialize(aggregate.EvidenceRefs, JsonOptions);
            report.CorrelationId = correlationId;
            report.Status = forceRegenerate ? "regenerating" : "generating";
            await _reports.UpdateAsync(report, ct);
            await _db.SaveChangesAsync(ct);
        }

        WeeklyReportSummaryResult summary;
        try
        {
            summary = await _summaries.GenerateAsync(
                tenantId, studentId, report.WeeklyReportId, aggregate, correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeeklyReportSummaryGenerator failed; marking report failed");
            report.Status = "failed";
            await _reports.UpdateAsync(report, ct);
            await _db.SaveChangesAsync(ct);
            return new WeeklyReportGenerationResult(report.WeeklyReportId, WeeklyReportGenerationOutcome.Failed);
        }

        if (summary.FinalStage == "refuse")
        {
            report.SummaryAr = summary.SummaryAr;
            report.SummaryEn = summary.SummaryEn;
            report.GuardrailDecisionTrailId = summary.GuardrailDecisionTrailId;
            report.Status = "failed";
            await _reports.UpdateAsync(report, ct);
            await _db.SaveChangesAsync(ct);
            return new WeeklyReportGenerationResult(report.WeeklyReportId, WeeklyReportGenerationOutcome.Failed);
        }

        report.SummaryAr = summary.SummaryAr;
        report.SummaryEn = summary.SummaryEn;
        report.GuardrailDecisionTrailId = summary.GuardrailDecisionTrailId;
        report.Status = "ready";
        report.GeneratedAt = DateTime.UtcNow;
        await _reports.UpdateAsync(report, ct);

        await _events.EmitGeneratedAsync(report, ct);

        if (_notificationHook is not null)
        {
            try
            {
                await _notificationHook.OnWeeklyReportReadyAsync(report, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "NotificationSchedulerHook.OnWeeklyReportReadyAsync threw for report {Report}; continuing",
                    report.WeeklyReportId);
            }
        }

        await _db.SaveChangesAsync(ct);

        return new WeeklyReportGenerationResult(
            report.WeeklyReportId,
            existing is null
                ? WeeklyReportGenerationOutcome.Generated
                : WeeklyReportGenerationOutcome.Regenerated);
    }
}

/// <summary>
/// Runtime options for <see cref="WeeklyReportGenerationJob"/>. Disabled by
/// default so test harnesses and the Phase 4 local smoke script drive
/// generation explicitly via <see cref="IWeeklyReportGenerator"/>.
/// </summary>
public sealed class WeeklyReportGenerationJobOptions
{
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
}

public sealed class WeeklyReportGenerationJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WeeklyReportGenerationJob> _logger;
    private readonly WeeklyReportGenerationJobOptions _options;

    public WeeklyReportGenerationJob(
        IServiceProvider services,
        ILogger<WeeklyReportGenerationJob> logger,
        IOptions<WeeklyReportGenerationJobOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("WeeklyReportGenerationJob disabled; skipping tick loop");
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
                _logger.LogError(ex, "WeeklyReportGenerationJob tick failed");
            }

            try { await Task.Delay(_options.Interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var generator = scope.ServiceProvider.GetRequiredService<IWeeklyReportGenerator>();

        var today = DateTime.UtcNow.Date;
        var windowEnd = today.AddDays(-1);
        var windowStart = windowEnd.AddDays(-6);

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
                await generator.GenerateAsync(
                    link.TenantId,
                    link.StudentId,
                    windowStart,
                    windowEnd,
                    correlationId,
                    forceRegenerate: false,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Weekly report generation failed for tenant={Tenant} student={Student}",
                    link.TenantId, link.StudentId);
            }
        }
    }
}

public static class WeeklyReportGenerationJobServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4WeeklyReportGenerator(this IServiceCollection services)
    {
        services.AddScoped<IWeeklyReportGenerator, WeeklyReportGenerator>();
        return services;
    }

    public static IServiceCollection AddPhase4WeeklyReportGenerationJob(this IServiceCollection services)
    {
        services.AddSingleton<IHostedService, WeeklyReportGenerationJob>();
        return services;
    }
}
