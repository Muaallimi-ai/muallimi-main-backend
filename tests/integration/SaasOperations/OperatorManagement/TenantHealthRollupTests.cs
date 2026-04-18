using Microsoft.EntityFrameworkCore;
using Muallimi.Api.OperatorManagement.TenantHealth;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.SaasOperations;
using Muallimi.Domain.SchoolManagement;
using Muallimi.Domain.StudentExperience;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.Api.Tests.Integration.SaasOperations.OperatorManagement;

public class TenantHealthRollupTests
{
    [Fact]
    public async Task RefreshAsync_aggregates_students_sessions_ai_cost_and_atrisk()
    {
        var db = Phase6TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var recent = now.AddDays(-5);

        db.StudentProfiles.Add(new StudentProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = "s1",
            CurriculumType = "moe",
            Grade = "5",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.StudentProfiles.Add(new StudentProfile
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = "s2",
            CurriculumType = "moe",
            Grade = "5",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Set<StudentSession>().Add(new StudentSession
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            StudentProfileId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            SessionStartedAt = recent,
            SessionLastActivityAt = recent,
        });
        db.Phase6AIOperationsMetrics.Add(new AIOperationsMetric
        {
            MetricId = Guid.NewGuid(),
            TenantId = tenantId,
            EstimatedCostEgp = 12.5m,
            OccurredAt = recent,
        });
        db.Set<AtRiskFlag>().Add(new AtRiskFlag
        {
            AtRiskFlagId = Guid.NewGuid(),
            TenantId = tenantId,
            StudentId = Guid.NewGuid(),
            RaisedAt = recent,
        });
        await db.SaveChangesAsync();

        var rollup = new TenantHealthRollupService(db);
        var view = await rollup.RefreshAsync(tenantId, CancellationToken.None);

        Assert.Equal(2, view.ActiveStudentCount);
        Assert.Equal(1, view.MonthlySessionCount);
        Assert.Equal(12.5m, view.MonthlyAiCostEgp);
        Assert.Equal(1, view.AtRiskStudentCount);
        Assert.Equal("family", view.TenantType);
        Assert.Equal("none", view.SubscriptionStatus);
    }

    [Fact]
    public async Task RefreshAsync_marks_school_tenants_and_reads_subscription_status()
    {
        var db = Phase6TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolNameAr = "مدرسة",
            SchoolNameEn = "School",
            CreatedAt = now,
            UpdatedAt = now,
        });
        var planId = Guid.NewGuid();
        db.SubscriptionPlans.Add(new SubscriptionPlan
        {
            PlanId = planId,
            PlanNameAr = "قياسية",
            PlanNameEn = "Standard",
            Tier = "premium",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Subscriptions.Add(new Subscription
        {
            SubscriptionId = Guid.NewGuid(),
            TenantId = tenantId,
            PlanId = planId,
            Status = "active",
            CurrentPeriodStart = now.AddDays(-10),
            CurrentPeriodEnd = now.AddDays(20),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var view = await new TenantHealthRollupService(db).RefreshAsync(tenantId, CancellationToken.None);

        Assert.Equal("school", view.TenantType);
        Assert.Equal("active", view.SubscriptionStatus);
        Assert.Equal("premium", view.PlanTier);
    }
}
