using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.AiOperations.AlertRuleEngine;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Domain.SaasOperations;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.AiOperations;

public class AlertRuleEvaluatorTests
{
    [Fact]
    public async Task RunOnceAsync_fires_alert_when_cost_above_threshold()
    {
        var db = Phase6TestDbContextFactory.Create();
        var channelSpy = new SpyChannelAdapter();
        var registry = new NotificationChannelAdapterRegistry(new INotificationChannelAdapter[] { channelSpy });
        var evaluator = new AlertRuleEvaluator(db, registry, NullLogger<AlertRuleEvaluator>.Instance);

        var tenant = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Phase6AIOperationsMetrics.Add(new AIOperationsMetric
        {
            MetricId = Guid.NewGuid(),
            TenantId = tenant,
            Phase = "phase2_tutor",
            PromptKey = "tutor.answer",
            PromptVersion = "v1",
            ProviderName = "anthropic",
            RequestCount = 1,
            TotalInputTokens = 100,
            TotalOutputTokens = 200,
            EstimatedCostEgp = 42m,
            LatencyMs = 200,
            GuardrailOutcome = "pass",
            WasRefusal = false,
            CorrelationId = "corr-a",
            OccurredAt = now.AddMinutes(-5),
        });
        db.AlertRules.Add(new AlertRule
        {
            RuleId = Guid.NewGuid(),
            RuleName = "cost-guard",
            MetricType = "ai_cost",
            ThresholdValue = 10m,
            ThresholdDirection = "above",
            EvaluationWindowMin = 15,
            CooldownMin = 60,
            TenantScope = null,
            NotificationTargets = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { OperatorId = Guid.NewGuid(), Channel = "in_app" },
            }),
            IsActive = true,
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var fired = await evaluator.RunOnceAsync();

        Assert.Equal(1, fired);
        var evt = Assert.Single(db.AlertEvents);
        Assert.Equal(42m, evt.TriggeringValue);
        Assert.Equal("open", evt.ResolutionStatus);
        Assert.NotEmpty(channelSpy.Dispatched);
    }

    [Fact]
    public async Task RunOnceAsync_respects_cooldown_and_does_not_refire()
    {
        var db = Phase6TestDbContextFactory.Create();
        var registry = new NotificationChannelAdapterRegistry(new INotificationChannelAdapter[] { new SpyChannelAdapter() });
        var evaluator = new AlertRuleEvaluator(db, registry, NullLogger<AlertRuleEvaluator>.Instance);

        var now = DateTime.UtcNow;
        var ruleId = Guid.NewGuid();
        db.AlertRules.Add(new AlertRule
        {
            RuleId = ruleId,
            RuleName = "cooldown-test",
            MetricType = "ai_cost",
            ThresholdValue = 1m,
            ThresholdDirection = "above",
            EvaluationWindowMin = 15,
            CooldownMin = 30,
            NotificationTargets = "[]",
            IsActive = true,
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.AlertEvents.Add(new AlertEvent
        {
            AlertEventId = Guid.NewGuid(),
            RuleId = ruleId,
            TriggeringValue = 5m,
            ThresholdValue = 1m,
            ResolutionStatus = "open",
            FiredAt = now.AddMinutes(-5),
        });
        db.Phase6AIOperationsMetrics.Add(new AIOperationsMetric
        {
            MetricId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Phase = "phase2_tutor",
            PromptKey = "p",
            PromptVersion = "v",
            ProviderName = "x",
            EstimatedCostEgp = 100m,
            LatencyMs = 10,
            GuardrailOutcome = "pass",
            CorrelationId = "c",
            OccurredAt = now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var fired = await evaluator.RunOnceAsync();

        Assert.Equal(0, fired);
        Assert.Single(db.AlertEvents);
    }

    private sealed class SpyChannelAdapter : INotificationChannelAdapter
    {
        public string Channel => "in_app";
        public List<NotificationDispatchRequest> Dispatched { get; } = new();
        public Task<NotificationDispatchReceipt> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default)
        {
            Dispatched.Add(request);
            return Task.FromResult(new NotificationDispatchReceipt("ok", Channel));
        }
    }
}
