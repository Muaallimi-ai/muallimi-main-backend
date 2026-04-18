using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Domain.Engagement;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Parents.ParentNotifications;

/// <summary>
/// T133 (US7) — Typed scheduler hook that routes Phase 4 job signals into
/// the <see cref="IParentNotificationDispatcher"/>.
///
/// Two entry points are exposed: one for the weekly-report generator (called
/// from <c>WeeklyReportGenerator.GenerateAsync</c> on the happy path) and
/// one reserved for the at-risk detection job shipping in US8. Both expand a
/// single student event into one dispatch per active parent linked to that
/// student — sibling and co-parent families both receive a row. The hook
/// also enqueues a <c>weekly_window_inactive</c> nudge when the report
/// shows zero engagement for the window.
///
/// Per the contract every notification carries the originating event's
/// correlation identifier so a support operator can trace the chain from
/// downstream event → notification row → channel delivery receipt.
/// </summary>
public interface INotificationSchedulerHook
{
    Task<IReadOnlyList<ParentNotificationDispatchOutcome>> OnWeeklyReportReadyAsync(
        WeeklyReport report,
        CancellationToken ct = default);

    Task<IReadOnlyList<ParentNotificationDispatchOutcome>> OnAtRiskFlaggedAsync(
        Guid tenantId,
        Guid studentId,
        Guid atRiskFlagId,
        Guid? interventionPromptId,
        string correlationId,
        CancellationToken ct = default);
}

public sealed class NotificationSchedulerHook : INotificationSchedulerHook
{
    private readonly MuallimiDbContext _db;
    private readonly IChildLinkRepository _links;
    private readonly IParentNotificationDispatcher _dispatcher;
    private readonly ILogger<NotificationSchedulerHook> _logger;

    public NotificationSchedulerHook(
        MuallimiDbContext db,
        IChildLinkRepository links,
        IParentNotificationDispatcher dispatcher,
        ILogger<NotificationSchedulerHook> logger)
    {
        _db = db;
        _links = links;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ParentNotificationDispatchOutcome>> OnWeeklyReportReadyAsync(
        WeeklyReport report,
        CancellationToken ct = default)
    {
        if (report.Status != "ready") return Array.Empty<ParentNotificationDispatchOutcome>();

        var parents = await _db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.TenantId == report.TenantId && l.StudentId == report.StudentId)
            .Where(l => l.EffectiveStart <= DateTime.UtcNow.Date
                        && (l.EffectiveEnd == null || l.EffectiveEnd >= DateTime.UtcNow.Date))
            .Select(l => l.ParentProfileId)
            .Distinct()
            .ToListAsync(ct);

        if (parents.Count == 0) return Array.Empty<ParentNotificationDispatchOutcome>();

        var kind = IsWindowInactive(report) ? "weekly_window_inactive" : "weekly_report_ready";
        var deepLink = $"/reports/{report.WeeklyReportId:D}";
        var outcomes = new List<ParentNotificationDispatchOutcome>(parents.Count);
        foreach (var parentProfileId in parents)
        {
            try
            {
                var outcome = await _dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
                    TenantId: report.TenantId,
                    ParentProfileId: parentProfileId,
                    ChildId: report.StudentId,
                    NotificationKind: kind,
                    BodyAr: report.SummaryAr,
                    BodyEn: report.SummaryEn,
                    DeepLink: deepLink,
                    CorrelationId: report.CorrelationId), ct);
                outcomes.Add(outcome);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "NotificationSchedulerHook.WeeklyReport failed for tenant={Tenant} parent={Parent} report={Report}",
                    report.TenantId, parentProfileId, report.WeeklyReportId);
            }
        }
        return outcomes;
    }

    public async Task<IReadOnlyList<ParentNotificationDispatchOutcome>> OnAtRiskFlaggedAsync(
        Guid tenantId,
        Guid studentId,
        Guid atRiskFlagId,
        Guid? interventionPromptId,
        string correlationId,
        CancellationToken ct = default)
    {
        var parents = await _db.ChildLinks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.StudentId == studentId)
            .Where(l => l.EffectiveStart <= DateTime.UtcNow.Date
                        && (l.EffectiveEnd == null || l.EffectiveEnd >= DateTime.UtcNow.Date))
            .Select(l => l.ParentProfileId)
            .Distinct()
            .ToListAsync(ct);

        if (parents.Count == 0) return Array.Empty<ParentNotificationDispatchOutcome>();

        var deepLink = interventionPromptId is null ? null : $"/interventions/{interventionPromptId:D}";
        var outcomes = new List<ParentNotificationDispatchOutcome>(parents.Count);
        foreach (var parentProfileId in parents)
        {
            try
            {
                var outcome = await _dispatcher.EnqueueAsync(new ParentNotificationDispatchInput(
                    TenantId: tenantId,
                    ParentProfileId: parentProfileId,
                    ChildId: studentId,
                    NotificationKind: "at_risk_flagged",
                    BodyAr: null,
                    BodyEn: null,
                    DeepLink: deepLink,
                    CorrelationId: correlationId), ct);
                outcomes.Add(outcome);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "NotificationSchedulerHook.AtRisk failed for tenant={Tenant} parent={Parent} flag={Flag}",
                    tenantId, parentProfileId, atRiskFlagId);
            }
        }
        return outcomes;
    }

    private static bool IsWindowInactive(WeeklyReport report)
    {
        if (string.IsNullOrWhiteSpace(report.EvidenceRefs)) return false;
        // The weekly-report aggregator writes an evidence_refs array whose
        // length reflects the number of contributing session events. An empty
        // array → the window saw no qualifying activity, which the contract
        // maps to the `weekly_window_inactive` nudge.
        return report.EvidenceRefs.Trim() is "[]";
    }
}

public static class NotificationSchedulerHookServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4NotificationSchedulerHook(this IServiceCollection services)
    {
        services.AddScoped<INotificationSchedulerHook, NotificationSchedulerHook>();
        return services;
    }
}
