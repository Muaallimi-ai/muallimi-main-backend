using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Compliance.AuditTrail;
using Muallimi.Api.OperatorManagement.LaunchReadinessGate;
using Muallimi.Domain.SaasOperations;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.OperatorManagement;

/// <summary>
/// T123 (US9) — Verifies the launch-readiness gate evaluator runs each
/// criterion, persists the gate row, and emits the audit entry for the
/// evaluation trigger.
/// </summary>
public class LaunchReadinessGateEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_returns_fail_on_clean_db_and_persists_gate_row()
    {
        var db = Phase6TestDbContextFactory.Create();
        var evaluator = new LaunchReadinessGateEvaluator(db, new AuditTrailWriter(db));
        var operatorId = Guid.NewGuid();

        var result = await evaluator.EvaluateAsync(operatorId, "corr-1", CancellationToken.None);

        Assert.Equal("fail", result.OverallStatus);
        Assert.NotEmpty(result.CriteriaResults);
        Assert.Contains(result.CriteriaResults, c => c.Category == "security");
        Assert.Contains(result.CriteriaResults, c => c.Category == "compliance");

        var persisted = await db.LaunchReadinessGates.FirstOrDefaultAsync(
            g => g.GateId == result.GateId);
        Assert.NotNull(persisted);
        Assert.Equal("fail", persisted!.OverallStatus);
        Assert.Equal(operatorId, persisted.EvaluatedBy);

        var audit = await db.AuditEntries.FirstOrDefaultAsync(
            a => a.ActionType == "operator.launch_readiness.evaluated"
              && a.TargetId == result.GateId);
        Assert.NotNull(audit);
        Assert.Equal("corr-1", audit!.CorrelationId);
    }

    [Fact]
    public async Task EvaluateAsync_passes_when_all_evidence_present()
    {
        var db = Phase6TestDbContextFactory.Create();
        var now = DateTime.UtcNow;
        var operatorId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolNameAr = "مدرسة",
            SchoolNameEn = "School",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SubscriptionPlans.Add(new SubscriptionPlan
        {
            PlanId = Guid.NewGuid(),
            PlanNameAr = "قياسية",
            PlanNameEn = "Standard",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Invoices.Add(new Invoice
        {
            InvoiceId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            TenantId = tenantId,
            InvoiceNumber = "INV-1",
            IssuedAt = now,
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            TransactionId = Guid.NewGuid(),
            InvoiceId = Guid.NewGuid(),
            SubscriptionId = Guid.NewGuid(),
            TenantId = tenantId,
            ProviderName = "local",
            IdempotencyKey = "k1",
            CorrelationId = "c1",
            AttemptedAt = now,
        });
        db.NotificationProviderBindings.Add(new NotificationProviderBinding
        {
            BindingId = Guid.NewGuid(),
            Channel = "email",
            ProviderName = "local",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.AlertRules.Add(new AlertRule
        {
            RuleId = Guid.NewGuid(),
            RuleName = "cost",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        for (var i = 0; i < 6; i++)
        {
            db.DataRetentionPolicies.Add(new DataRetentionPolicy
            {
                PolicyId = Guid.NewGuid(),
                EntityType = $"ent_{i}",
                RetentionDays = 90,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();

        var evaluator = new LaunchReadinessGateEvaluator(db, new AuditTrailWriter(db));
        var result = await evaluator.EvaluateAsync(operatorId, "corr-2", CancellationToken.None);

        var dbDrivenPassed = new[]
        {
            "phase_0_5_readiness",
            "security_audit",
            "pii_encryption",
            "billing_e2e",
            "notification_delivery",
            "observability_dashboard",
            "data_protection",
        };
        foreach (var key in dbDrivenPassed)
        {
            Assert.Equal("pass", result.CriteriaResults.First(c => c.Criterion == key).Status);
        }
    }

    [Fact]
    public async Task RecordGoLiveSignOffAsync_writes_audit_entry()
    {
        var db = Phase6TestDbContextFactory.Create();
        var evaluator = new LaunchReadinessGateEvaluator(db, new AuditTrailWriter(db));
        var gateId = Guid.NewGuid();
        var operatorId = Guid.NewGuid();

        await evaluator.RecordGoLiveSignOffAsync(operatorId, gateId, "ready", "corr-3", CancellationToken.None);

        var audit = await db.AuditEntries.FirstOrDefaultAsync(
            a => a.ActionType == "operator.launch_readiness.go_live_signed_off"
              && a.TargetId == gateId);
        Assert.NotNull(audit);
        Assert.Equal(operatorId, audit!.ActorId);
        Assert.Equal("corr-3", audit.CorrelationId);
    }
}
