using System;
using Muallimi.Domain.Shared;

namespace Muallimi.Domain.SaasOperations;

/// <summary>
/// Phase 6 — SaaS Operations, Billing, Security, and Launch Readiness entities.
/// See specs/008-saas-operations-launch/data-model.md.
/// </summary>
public class SubscriptionPlan
{
    public Guid PlanId { get; set; }
    public string PlanNameAr { get; set; } = string.Empty;
    public string PlanNameEn { get; set; } = string.Empty;
    public string PlanType { get; set; } = "family"; // family | school
    public string Tier { get; set; } = "free"; // free | standard | premium
    public decimal PriceEgp { get; set; }
    public decimal? PriceUsd { get; set; }
    public string BillingCycle { get; set; } = "monthly"; // monthly | yearly
    public int? SeatLimit { get; set; }
    public string FeatureEntitlements { get; set; } = "{}";
    public string UsageLimits { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public Guid CreatedByOperatorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Subscription : ITenantScoped
{
    public Guid SubscriptionId { get; set; }
    public Guid TenantId { get; set; }
    public Guid PlanId { get; set; }
    public string PlanType { get; set; } = "family";
    public string Status { get; set; } = "trial"; // trial | active | grace | expired | cancelled
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public DateTime? TrialEnd { get; set; }
    public DateTime? GracePeriodEnd { get; set; }
    public string? PaymentMethodRef { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Invoice : ITenantScoped
{
    public Guid InvoiceId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid TenantId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string LineItems { get; set; } = "[]";
    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "egp"; // egp | usd
    public string PaymentStatus { get; set; } = "pending"; // pending | paid | failed | refunded
    public string? PdfBlobKey { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? PaidAt { get; set; }
}

public class PaymentTransaction : ITenantScoped
{
    public Guid TransactionId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid TenantId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "egp";
    public string TransactionType { get; set; } = "charge"; // charge | refund
    public string Status { get; set; } = "pending"; // pending | success | failed | refunded
    public string? FailureReason { get; set; }
    public string? FailureCode { get; set; }
    public string? WebhookPayload { get; set; } // encrypted
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime AttemptedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class NotificationProviderBinding
{
    public Guid BindingId { get; set; }
    public string Channel { get; set; } = "email"; // email | push | whatsapp | in_app
    public string ProviderName { get; set; } = string.Empty;
    public string Environment { get; set; } = "local"; // local | production
    public string Configuration { get; set; } = "{}"; // encrypted
    public int? RateLimitPerMinute { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class NotificationDeliveryReceipt : ITenantScoped
{
    public Guid ReceiptId { get; set; }
    public Guid TenantId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid RecipientId { get; set; }
    public string Channel { get; set; } = "email";
    public string ProviderName { get; set; } = string.Empty;
    public string Status { get; set; } = "queued"; // queued | sent | delivered | failed | dead_lettered
    public string? ProviderMessageId { get; set; }
    public string? FailureReason { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime DispatchedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
}

public class AIOperationsMetric : ITenantScoped
{
    public Guid MetricId { get; set; }
    public Guid TenantId { get; set; }
    public string Phase { get; set; } = "phase2_tutor";
    public string PromptKey { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public int RequestCount { get; set; } = 1;
    public int TotalInputTokens { get; set; }
    public int TotalOutputTokens { get; set; }
    public decimal EstimatedCostEgp { get; set; }
    public int LatencyMs { get; set; }
    public string GuardrailOutcome { get; set; } = "not_applicable";
    public decimal? ConfidenceScore { get; set; }
    public bool WasRefusal { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
}

public class AIOperationsAggregate
{
    public Guid AggregateId { get; set; }
    public Guid? TenantId { get; set; }
    public string? Phase { get; set; }
    public string? PromptKey { get; set; }
    public string PeriodType { get; set; } = "hourly"; // hourly | daily | weekly
    public DateTime PeriodStart { get; set; }
    public int RequestCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public decimal TotalCostEgp { get; set; }
    public int AvgLatencyMs { get; set; }
    public int P95LatencyMs { get; set; }
    public int P99LatencyMs { get; set; }
    public int GuardrailPassCount { get; set; }
    public int GuardrailWarnCount { get; set; }
    public int GuardrailBlockCount { get; set; }
    public int RefusalCount { get; set; }
    public DateTime ComputedAt { get; set; }
}

public class AlertRule
{
    public Guid RuleId { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string MetricType { get; set; } = "ai_cost"; // ai_cost | ai_latency | error_rate | guardrail_block_rate | queue_depth
    public decimal ThresholdValue { get; set; }
    public string ThresholdDirection { get; set; } = "above"; // above | below
    public int EvaluationWindowMin { get; set; }
    public int CooldownMin { get; set; }
    public Guid? TenantScope { get; set; }
    public string NotificationTargets { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public Guid CreatedByOperatorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class AlertEvent
{
    public Guid AlertEventId { get; set; }
    public Guid RuleId { get; set; }
    public decimal TriggeringValue { get; set; }
    public decimal ThresholdValue { get; set; }
    public string? AffectedTenants { get; set; }
    public string? SampleCorrelationIds { get; set; }
    public string ResolutionStatus { get; set; } = "open"; // open | acknowledged | resolved
    public Guid? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNotes { get; set; }
    public DateTime FiredAt { get; set; }
}

public class IncidentRecord
{
    public Guid IncidentId { get; set; }
    public string Severity { get; set; } = "medium"; // critical | high | medium | low
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AffectedServices { get; set; } = "[]";
    public string? AffectedTenants { get; set; }
    public string? RootCause { get; set; }
    public string? Resolution { get; set; }
    public string? RunbookReference { get; set; }
    public string Status { get; set; } = "open"; // open | investigating | mitigated | resolved
    public Guid OpenedBy { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? MitigatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string Timeline { get; set; } = "[]";
    public string CorrelationId { get; set; } = string.Empty;
}

/// <summary>
/// Immutable audit record. No UPDATE or DELETE permitted at the application layer.
/// </summary>
public class AuditEntry : ITenantScoped
{
    public Guid AuditEntryId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorId { get; set; }
    public string ActorType { get; set; } = "system";
    public Guid? TargetId { get; set; }
    public string? TargetType { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string? Payload { get; set; } // PII-masked
    public string? IpAddress { get; set; } // masked for child accounts
    public string? UserAgent { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
}

public class DataDeletionRequest : ITenantScoped
{
    public Guid DeletionRequestId { get; set; }
    public Guid TenantId { get; set; }
    public string TargetScope { get; set; } = "student"; // student | parent | tenant
    public Guid TargetId { get; set; }
    public Guid RequestedBy { get; set; }
    public string Status { get; set; } = "pending"; // pending | processing | completed | failed
    public string? TablesProcessed { get; set; }
    public string? ErrorDetails { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ConfirmationSentAt { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public class DataRetentionPolicy
{
    public Guid PolicyId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int RetentionDays { get; set; }
    public string AnonymisationRule { get; set; } = "delete"; // delete | anonymise | archive
    public bool IsActive { get; set; } = true;
    public DateTime? LastExecutedAt { get; set; }
    public int? RowsAffectedLastRun { get; set; }
    public Guid CreatedByOperatorId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class FeatureFlag : ITenantScoped
{
    public Guid FeatureFlagId { get; set; }
    public Guid TenantId { get; set; }
    public string FlagName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public Guid ChangedByOperatorId { get; set; }
    public DateTime ChangedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LaunchReadinessGate
{
    public Guid GateId { get; set; }
    public string EvaluationName { get; set; } = string.Empty;
    public string CriteriaResults { get; set; } = "[]";
    public string OverallStatus { get; set; } = "fail"; // pass | fail
    public Guid EvaluatedBy { get; set; }
    public DateTime EvaluatedAt { get; set; }
}

public class TenantHealthView
{
    public Guid TenantHealthId { get; set; }
    public Guid TenantId { get; set; }
    public string TenantType { get; set; } = "family";
    public string SubscriptionStatus { get; set; } = "none";
    public string PlanTier { get; set; } = "free";
    public int ActiveStudentCount { get; set; }
    public int MonthlySessionCount { get; set; }
    public decimal MonthlyAiCostEgp { get; set; }
    public int StorageUsageMb { get; set; }
    public decimal? EngagementScore { get; set; }
    public int AtRiskStudentCount { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime ComputedAt { get; set; }
}

public class Phase6OperationalEvent
{
    public Guid EventId { get; set; }
    public Guid TenantId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public string SchemaVersion { get; set; } = "1.0.0";
}
