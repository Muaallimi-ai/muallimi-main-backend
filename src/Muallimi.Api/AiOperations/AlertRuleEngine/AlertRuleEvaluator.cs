using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Application.Notifications.Channels;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.AiOperations.AlertRuleEngine;

/// <summary>
/// T068 (US3) — Evaluates active <see cref="AlertRule"/> rows against the
/// AI operations metric window, fires <see cref="AlertEvent"/> rows when the
/// threshold is breached, and dispatches an operator notification through
/// the existing <see cref="INotificationChannelAdapterRegistry"/>.
///
/// Metrics supported (from ai-operations-contract.md):
///   ai_cost              — sum of estimated_cost_egp in window
///   ai_latency           — avg latency_ms in window
///   error_rate           — refusal_count / request_count in window (0..1)
///   guardrail_block_rate — block_count / request_count in window (0..1)
///   queue_depth          — NOT tracked in ai-ops metrics; evaluator skips it
///
/// Cooldown: a rule that fires an alert will not fire again until
/// <c>cooldown_min</c> minutes have elapsed since the last open/unresolved
/// <see cref="AlertEvent"/> for that rule.
/// </summary>
public interface IAlertRuleEvaluator
{
    Task<int> RunOnceAsync(CancellationToken ct = default);
}

public sealed class AlertRuleEvaluator : IAlertRuleEvaluator
{
    private readonly MuallimiDbContext _db;
    private readonly INotificationChannelAdapterRegistry _channels;
    private readonly ILogger<AlertRuleEvaluator> _logger;

    public AlertRuleEvaluator(
        MuallimiDbContext db,
        INotificationChannelAdapterRegistry channels,
        ILogger<AlertRuleEvaluator> logger)
    {
        _db = db;
        _channels = channels;
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var activeRules = await _db.AlertRules.Where(r => r.IsActive).ToListAsync(ct);
        var fired = 0;

        foreach (var rule in activeRules)
        {
            ct.ThrowIfCancellationRequested();
            var inCooldown = await IsInCooldownAsync(rule, now, ct);
            if (inCooldown) continue;

            var windowStart = now.AddMinutes(-Math.Max(1, rule.EvaluationWindowMin));
            var value = await ComputeValueAsync(rule, windowStart, now, ct);
            if (value is null) continue;

            var breached = EvaluateBreach(rule.ThresholdDirection, value.Value, rule.ThresholdValue);
            if (!breached) continue;

            await FireAsync(rule, value.Value, windowStart, now, ct);
            fired++;
        }
        return fired;
    }

    private async Task<bool> IsInCooldownAsync(AlertRule rule, DateTime now, CancellationToken ct)
    {
        if (rule.CooldownMin <= 0) return false;
        var cutoff = now.AddMinutes(-rule.CooldownMin);
        return await _db.AlertEvents.AnyAsync(e => e.RuleId == rule.RuleId && e.FiredAt >= cutoff, ct);
    }

    private async Task<decimal?> ComputeValueAsync(AlertRule rule, DateTime windowStart, DateTime now, CancellationToken ct)
    {
        var q = _db.Phase6AIOperationsMetrics
            .IgnoreQueryFilters()
            .Where(m => m.OccurredAt >= windowStart && m.OccurredAt <= now);
        if (rule.TenantScope.HasValue)
        {
            q = q.Where(m => m.TenantId == rule.TenantScope.Value);
        }

        var rows = await q.Select(m => new
        {
            m.EstimatedCostEgp,
            m.LatencyMs,
            m.GuardrailOutcome,
            m.WasRefusal,
        }).ToListAsync(ct);

        if (rows.Count == 0) return null;

        return rule.MetricType switch
        {
            "ai_cost" => rows.Sum(r => r.EstimatedCostEgp),
            "ai_latency" => (decimal)rows.Average(r => r.LatencyMs),
            "error_rate" => (decimal)rows.Count(r => r.WasRefusal) / rows.Count,
            "guardrail_block_rate" => (decimal)rows.Count(r => r.GuardrailOutcome == "block") / rows.Count,
            _ => null,
        };
    }

    private static bool EvaluateBreach(string direction, decimal value, decimal threshold)
        => string.Equals(direction, "above", StringComparison.OrdinalIgnoreCase)
            ? value > threshold
            : value < threshold;

    private async Task FireAsync(AlertRule rule, decimal value, DateTime windowStart, DateTime now, CancellationToken ct)
    {
        var evt = new AlertEvent
        {
            AlertEventId = Guid.NewGuid(),
            RuleId = rule.RuleId,
            TriggeringValue = value,
            ThresholdValue = rule.ThresholdValue,
            AffectedTenants = rule.TenantScope.HasValue
                ? JsonSerializer.Serialize(new[] { rule.TenantScope.Value })
                : null,
            SampleCorrelationIds = null,
            ResolutionStatus = "open",
            ResolvedBy = null,
            ResolvedAt = null,
            ResolutionNotes = null,
            FiredAt = now,
        };
        _db.AlertEvents.Add(evt);
        await _db.SaveChangesAsync(ct);

        await DispatchOperatorAlertsAsync(rule, evt, ct);
    }

    private async Task DispatchOperatorAlertsAsync(AlertRule rule, AlertEvent evt, CancellationToken ct)
    {
        IReadOnlyList<NotificationTarget> targets;
        try
        {
            targets = JsonSerializer.Deserialize<IReadOnlyList<NotificationTarget>>(rule.NotificationTargets)
                ?? Array.Empty<NotificationTarget>();
        }
        catch
        {
            targets = Array.Empty<NotificationTarget>();
        }

        foreach (var t in targets)
        {
            var channelKey = string.IsNullOrWhiteSpace(t.Channel) ? "in_app" : t.Channel;
            try
            {
                var adapter = _channels.Get(channelKey);
                await adapter.DispatchAsync(new NotificationDispatchRequest(
                    TenantId: rule.TenantScope ?? Guid.Empty,
                    RecipientUserId: t.OperatorId,
                    RecipientEmail: null,
                    NotificationKind: "operator.alert_fired",
                    Language: "ar",
                    Title: rule.RuleName,
                    Body: $"metric={rule.MetricType} value={evt.TriggeringValue} threshold={rule.ThresholdValue}",
                    Metadata: new Dictionary<string, string>
                    {
                        ["rule_id"] = rule.RuleId.ToString("D"),
                        ["alert_event_id"] = evt.AlertEventId.ToString("D"),
                    },
                    CorrelationId: evt.AlertEventId.ToString("N")), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Operator alert dispatch failed for rule {RuleId} channel {Channel}", rule.RuleId, channelKey);
            }
        }
    }

    private sealed record NotificationTarget(Guid OperatorId, string Channel);
}

public sealed class AlertRuleEvaluatorOptions
{
    public bool EnableBackgroundLoop { get; set; } = false;
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed class AlertRuleEvaluatorHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AlertRuleEvaluatorHostedService> _logger;
    private readonly AlertRuleEvaluatorOptions _options;

    public AlertRuleEvaluatorHostedService(
        IServiceProvider services,
        ILogger<AlertRuleEvaluatorHostedService> logger,
        IOptions<AlertRuleEvaluatorOptions> options)
    {
        _services = services;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundLoop) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAlertRuleEvaluator>();
                await svc.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AlertRuleEvaluatorHostedService tick failed");
            }
            await Task.Delay(_options.Interval, stoppingToken);
        }
    }
}

public static class AlertRuleEvaluatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase6AlertRuleEvaluator(this IServiceCollection services)
    {
        services.AddScoped<IAlertRuleEvaluator, AlertRuleEvaluator>();
        services.AddHostedService<AlertRuleEvaluatorHostedService>();
        return services;
    }
}
