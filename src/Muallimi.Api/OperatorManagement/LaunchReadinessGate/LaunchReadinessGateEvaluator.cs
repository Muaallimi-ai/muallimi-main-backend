using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Domain.SaasOperations;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.OperatorManagement.LaunchReadinessGate;

/// <summary>
/// T123 (US9) — Evaluates the launch-readiness gate by running each
/// criterion evidence check against the runtime database + well-known
/// evidence sources. Produces an overall pass/fail with per-criterion
/// result detail and persists a <see cref="LaunchReadinessGate"/> row.
/// </summary>
public class LaunchReadinessGateEvaluator
{
    private readonly MuallimiDbContext _db;
    private readonly AuditTrailWriter _audit;

    public LaunchReadinessGateEvaluator(MuallimiDbContext db, AuditTrailWriter audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<LaunchReadinessGateResult> EvaluateAsync(
        Guid operatorId,
        string correlationId,
        CancellationToken ct = default)
    {
        var results = new List<CriterionOutcome>();
        foreach (var criterion in LaunchReadinessCriteria.All)
        {
            var outcome = await EvaluateOneAsync(criterion, ct);
            results.Add(outcome);
        }

        var overall = results.All(r => r.Status == "pass") ? "pass" : "fail";
        var gateId = Guid.NewGuid();
        var evaluatedAt = DateTime.UtcNow;

        var entity = new Muallimi.Domain.SaasOperations.LaunchReadinessGate
        {
            GateId = gateId,
            EvaluationName = $"launch-readiness-{evaluatedAt:yyyyMMddHHmmss}",
            CriteriaResults = JsonSerializer.Serialize(results),
            OverallStatus = overall,
            EvaluatedBy = operatorId,
            EvaluatedAt = evaluatedAt,
        };
        _db.LaunchReadinessGates.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = Guid.Empty,
            ActorId = operatorId,
            ActorType = "operator",
            TargetId = gateId,
            TargetType = "launch_readiness_gate",
            ActionType = "operator.launch_readiness.evaluated",
            Payload = new
            {
                gate_id = gateId,
                overall_status = overall,
                pass_count = results.Count(r => r.Status == "pass"),
                fail_count = results.Count(r => r.Status == "fail"),
            },
            CorrelationId = correlationId,
        }, ct);

        return new LaunchReadinessGateResult
        {
            GateId = gateId,
            OverallStatus = overall,
            CriteriaResults = results,
            EvaluatedBy = operatorId,
            EvaluatedAt = evaluatedAt,
        };
    }

    public async Task RecordGoLiveSignOffAsync(
        Guid operatorId,
        Guid gateId,
        string? notes,
        string correlationId,
        CancellationToken ct = default)
    {
        await _audit.WriteAsync(new AuditTrailEntry
        {
            TenantId = Guid.Empty,
            ActorId = operatorId,
            ActorType = "operator",
            TargetId = gateId,
            TargetType = "launch_readiness_gate",
            ActionType = "operator.launch_readiness.go_live_signed_off",
            Payload = new { gate_id = gateId, notes },
            CorrelationId = correlationId,
        }, ct);
    }

    // ── Criterion evaluators ───────────────────────────────────────────────

    private async Task<CriterionOutcome> EvaluateOneAsync(
        LaunchReadinessCriterion criterion,
        CancellationToken ct)
    {
        try
        {
            return criterion.Key switch
            {
                "phase_0_5_readiness" => await CheckPhasesAsync(criterion, ct),
                "security_audit" => await CheckSecurityAuditAsync(criterion, ct),
                "auth_bypass_tests" => CheckAuthBypassTests(criterion),
                "pii_encryption" => await CheckPiiEncryptionAsync(criterion, ct),
                "performance_benchmarks" => CheckPerformanceBenchmarks(criterion),
                "arabic_quality" => CheckArabicQuality(criterion),
                "accessibility" => CheckAccessibility(criterion),
                "billing_e2e" => await CheckBillingE2eAsync(criterion, ct),
                "notification_delivery" => await CheckNotificationDeliveryAsync(criterion, ct),
                "observability_dashboard" => await CheckObservabilityAsync(criterion, ct),
                "runbook_documentation" => CheckRunbooks(criterion),
                "data_protection" => await CheckDataProtectionAsync(criterion, ct),
                _ => Fail(criterion, "Unknown criterion."),
            };
        }
        catch (Exception ex)
        {
            return Fail(criterion, $"Evaluator error: {ex.Message}");
        }
    }

    private static CriterionOutcome Pass(LaunchReadinessCriterion c, string? notes = null)
        => new()
        {
            Criterion = c.Key,
            NameAr = c.NameAr,
            NameEn = c.NameEn,
            Category = c.Category,
            Status = "pass",
            EvidenceLink = c.EvidenceSource,
            Notes = notes,
        };

    private static CriterionOutcome Fail(LaunchReadinessCriterion c, string notes)
        => new()
        {
            Criterion = c.Key,
            NameAr = c.NameAr,
            NameEn = c.NameEn,
            Category = c.Category,
            Status = "fail",
            EvidenceLink = c.EvidenceSource,
            Notes = notes,
        };

    private async Task<CriterionOutcome> CheckPhasesAsync(LaunchReadinessCriterion c, CancellationToken ct)
    {
        var hasPhase5 = await _db.SchoolTenants.IgnoreQueryFilters().AnyAsync(ct);
        var hasBillingPlans = await _db.SubscriptionPlans.AnyAsync(ct);
        if (!hasPhase5) return Fail(c, "No Phase 5 tenants provisioned.");
        if (!hasBillingPlans) return Fail(c, "No Phase 6 subscription plans seeded.");
        return Pass(c);
    }

    private async Task<CriterionOutcome> CheckSecurityAuditAsync(LaunchReadinessCriterion c, CancellationToken ct)
    {
        var hasRetention = await _db.DataRetentionPolicies.AnyAsync(p => p.IsActive, ct);
        return hasRetention ? Pass(c) : Fail(c, "No active data-retention policies.");
    }

    private static CriterionOutcome CheckAuthBypassTests(LaunchReadinessCriterion c)
    {
        var exists = Directory.Exists("tests/security")
            || Directory.Exists("../muallimi-main-backend/tests/security");
        return exists ? Pass(c) : Fail(c, "Security test suite directory missing.");
    }

    private async Task<CriterionOutcome> CheckPiiEncryptionAsync(LaunchReadinessCriterion c, CancellationToken ct)
    {
        var sample = await _db.PaymentTransactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.WebhookPayload != null && p.WebhookPayload != "")
            .Select(p => p.WebhookPayload)
            .Take(5)
            .ToListAsync(ct);
        if (sample.Count == 0) return Pass(c, "No webhook payloads yet (clean state).");
        var ok = sample.All(s => s!.StartsWith("enc:v", StringComparison.Ordinal));
        return ok ? Pass(c) : Fail(c, "Found webhook payload without encryption prefix.");
    }

    private static CriterionOutcome CheckPerformanceBenchmarks(LaunchReadinessCriterion c)
    {
        var path = c.EvidenceSource;
        return File.Exists(path) ? Pass(c) : Fail(c, "Performance evidence file not captured yet.");
    }

    private static CriterionOutcome CheckArabicQuality(LaunchReadinessCriterion c)
    {
        var candidates = new[]
        {
            "../Muaallimi-Platform/tests/e2e",
            "Muaallimi-Platform/tests/e2e",
        };
        return candidates.Any(Directory.Exists) ? Pass(c) : Fail(c, "Arabic quality suite directory missing.");
    }

    private static CriterionOutcome CheckAccessibility(LaunchReadinessCriterion c)
    {
        var candidates = new[]
        {
            "../Muaallimi-Platform/tests/e2e",
            "Muaallimi-Platform/tests/e2e",
        };
        return candidates.Any(Directory.Exists) ? Pass(c) : Fail(c, "Accessibility suite directory missing.");
    }

    private async Task<CriterionOutcome> CheckBillingE2eAsync(LaunchReadinessCriterion c, CancellationToken ct)
    {
        var invoiceCount = await _db.Invoices.IgnoreQueryFilters().CountAsync(ct);
        var txnCount = await _db.PaymentTransactions.IgnoreQueryFilters().CountAsync(ct);
        if (invoiceCount == 0) return Fail(c, "No invoices generated yet.");
        if (txnCount == 0) return Fail(c, "No payment transactions recorded yet.");
        return Pass(c);
    }

    private async Task<CriterionOutcome> CheckNotificationDeliveryAsync(LaunchReadinessCriterion c, CancellationToken ct)
    {
        var hasBinding = await _db.NotificationProviderBindings.AnyAsync(b => b.IsActive, ct);
        return hasBinding ? Pass(c) : Fail(c, "No active notification provider bindings.");
    }

    private async Task<CriterionOutcome> CheckObservabilityAsync(LaunchReadinessCriterion c, CancellationToken ct)
    {
        var hasAggregates = await _db.AIOperationsAggregates.AnyAsync(ct);
        var hasAlertRules = await _db.AlertRules.AnyAsync(r => r.IsActive, ct);
        if (!hasAlertRules) return Fail(c, "No active alert rules configured.");
        return hasAggregates ? Pass(c) : Pass(c, "No aggregates yet (clean state).");
    }

    private static CriterionOutcome CheckRunbooks(LaunchReadinessCriterion c)
    {
        var candidates = new[]
        {
            "docs/runbooks",
            "../muallimi-main-backend/docs/runbooks",
            "../Muaallimi-Platform-Planning-Docs-main/docs/runbooks",
        };
        return candidates.Any(Directory.Exists) ? Pass(c) : Fail(c, "Runbook directory missing.");
    }

    private async Task<CriterionOutcome> CheckDataProtectionAsync(LaunchReadinessCriterion c, CancellationToken ct)
    {
        var hasPolicies = await _db.DataRetentionPolicies.CountAsync(p => p.IsActive, ct) >= 5;
        return hasPolicies ? Pass(c) : Fail(c, "Fewer than five active retention policies.");
    }
}

public sealed record LaunchReadinessGateResult
{
    public required Guid GateId { get; init; }
    public required string OverallStatus { get; init; }
    public required IReadOnlyList<CriterionOutcome> CriteriaResults { get; init; }
    public required Guid EvaluatedBy { get; init; }
    public required DateTime EvaluatedAt { get; init; }
}

public sealed record CriterionOutcome
{
    public required string Criterion { get; init; }
    public required string NameAr { get; init; }
    public required string NameEn { get; init; }
    public required string Category { get; init; }
    public required string Status { get; init; }
    public string? EvidenceLink { get; init; }
    public string? Notes { get; init; }
}
