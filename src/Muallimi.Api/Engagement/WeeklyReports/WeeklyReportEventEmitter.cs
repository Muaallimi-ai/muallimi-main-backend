using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Domain.Engagement;

namespace Muallimi.Api.Engagement.WeeklyReports;

/// <summary>
/// T098 (US3) — Emits <c>weekly_report_generated</c> downstream events via
/// <see cref="IPhase4DownstreamEventOutbox"/>.
///
/// The payload mirrors the Phase 4 downstream events contract — consumers
/// MUST ignore unknown fields to preserve additive-only evolution. The
/// outbox row is written inside the same unit of work as the
/// <see cref="WeeklyReport"/> insert so notification dispatch and report
/// persistence cannot diverge.
/// </summary>
public interface IWeeklyReportEventEmitter
{
    Task EmitGeneratedAsync(
        WeeklyReport report,
        CancellationToken ct = default);
}

public sealed class WeeklyReportEventEmitter : IWeeklyReportEventEmitter
{
    private readonly IPhase4DownstreamEventOutbox _outbox;

    public WeeklyReportEventEmitter(IPhase4DownstreamEventOutbox outbox)
    {
        _outbox = outbox;
    }

    public Task EmitGeneratedAsync(WeeklyReport report, CancellationToken ct = default)
    {
        var scope = new
        {
            window_start = report.WindowStart.ToString("yyyy-MM-dd"),
            window_end = report.WindowEnd.ToString("yyyy-MM-dd"),
        };
        var payload = new
        {
            weekly_report_id = report.WeeklyReportId,
            run_id = report.RunId,
            status = report.Status,
            guardrail_decision_trail_id = report.GuardrailDecisionTrailId,
            generated_at = report.GeneratedAt,
        };
        return _outbox.EnqueueAsync(
            Phase4DownstreamEventKind.weekly_report_generated,
            report.TenantId,
            report.StudentId,
            scope,
            payload,
            report.CorrelationId,
            occurredAt: report.GeneratedAt,
            ct);
    }
}

public static class WeeklyReportEventEmitterServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4WeeklyReportEventEmitter(this IServiceCollection services)
    {
        services.AddScoped<IWeeklyReportEventEmitter, WeeklyReportEventEmitter>();
        return services;
    }
}
