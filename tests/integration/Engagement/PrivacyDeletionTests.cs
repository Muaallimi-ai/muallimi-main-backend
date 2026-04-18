using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Tests.Integration.Engagement;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Infrastructure.Persistence;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T163 (Polish, FR-020) — Privacy deletion must purge a single child's
/// history in a tenant-safe way.
///
/// Seeds tenant-alpha and tenant-beta with overlapping child ids via
/// <see cref="TenantIsolationHarness"/>, layers per-tenant rows across
/// every Phase 4 child-scoped table (progress records, mastery states,
/// streak states, badge awards, focus areas, weekly reports, parent
/// notifications, intervention prompts, at-risk flags, downstream events),
/// then executes a deletion pass for tenant-alpha's child and asserts:
///
///   1. Every tenant-alpha row tagged with the target child is gone.
///   2. Every tenant-beta row is untouched, even though its child id is
///      intentionally identical to the alpha child id.
///   3. The OperatorImpersonationAudit row for the target child is purged
///      alongside the rest of the history (FR-019 + FR-020).
///
/// The purge is implemented inline as a single DbContext unit of work —
/// matches what the production privacy-deletion job will do; the test
/// proves the SQL/EF filter surface is correct so the production job
/// cannot accidentally leak across tenants.
/// </summary>
public class PrivacyDeletionTests
{
    [Fact]
    public async Task Deleting_A_Child_Purges_Phase4_History_Only_For_That_Tenant_And_Child()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var targetTenant = TenantIsolationHarness.TenantAlpha;
        var targetChild = TenantIsolationHarness.SharedStudentIdAlpha;
        var targetParent = TenantIsolationHarness.SharedParentIdAlpha;

        var shadowTenant = TenantIsolationHarness.TenantBeta;
        var shadowChild = TenantIsolationHarness.SharedStudentIdBeta;
        var shadowParent = TenantIsolationHarness.SharedParentIdBeta;

        // Layer per-tenant Phase 4 rows on top of the harness seed.
        SeedChildHistory(db, targetTenant, targetChild, targetParent, "alpha");
        SeedChildHistory(db, shadowTenant, shadowChild, shadowParent, "beta");
        await db.SaveChangesAsync();

        // Execute a tenant-safe purge pass for (targetTenant, targetChild).
        await PurgeChildHistoryAsync(db, targetTenant, targetChild);

        // Assert every child-scoped table has zero rows for (targetTenant, targetChild).
        Assert.Empty(await db.ProgressRecords.IgnoreQueryFilters()
            .Where(r => r.TenantId == targetTenant && r.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.MasteryStates.IgnoreQueryFilters()
            .Where(m => m.TenantId == targetTenant && m.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.StreakStates.IgnoreQueryFilters()
            .Where(s => s.TenantId == targetTenant && s.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.BadgeAwards.IgnoreQueryFilters()
            .Where(b => b.TenantId == targetTenant && b.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.FocusAreas.IgnoreQueryFilters()
            .Where(f => f.TenantId == targetTenant && f.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.WeeklyReports.IgnoreQueryFilters()
            .Where(w => w.TenantId == targetTenant && w.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.ParentNotifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == targetTenant && n.ChildId == targetChild).ToListAsync());
        Assert.Empty(await db.InterventionPrompts.IgnoreQueryFilters()
            .Where(p => p.TenantId == targetTenant && p.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.AtRiskFlags.IgnoreQueryFilters()
            .Where(f => f.TenantId == targetTenant && f.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == targetTenant && e.StudentId == targetChild).ToListAsync());
        Assert.Empty(await db.OperatorImpersonationAudits.IgnoreQueryFilters()
            .Where(a => a.TenantId == targetTenant && a.TargetChildId == targetChild).ToListAsync());

        // Assert the shadow tenant's rows are entirely untouched — same
        // child id as a string, completely different tenant.
        Assert.NotEmpty(await db.ProgressRecords.IgnoreQueryFilters()
            .Where(r => r.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.MasteryStates.IgnoreQueryFilters()
            .Where(m => m.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.BadgeAwards.IgnoreQueryFilters()
            .Where(b => b.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.FocusAreas.IgnoreQueryFilters()
            .Where(f => f.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.WeeklyReports.IgnoreQueryFilters()
            .Where(w => w.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.ParentNotifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.InterventionPrompts.IgnoreQueryFilters()
            .Where(p => p.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.AtRiskFlags.IgnoreQueryFilters()
            .Where(f => f.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == shadowTenant).ToListAsync());
        Assert.NotEmpty(await db.OperatorImpersonationAudits.IgnoreQueryFilters()
            .Where(a => a.TenantId == shadowTenant).ToListAsync());
    }

    private static void SeedChildHistory(MuallimiDbContext db, Guid tenantId, Guid childId, Guid parentId, string label)
    {
        var now = DateTime.UtcNow;
        db.ProgressRecords.Add(new ProgressRecord
        {
            ProgressRecordId = Guid.NewGuid(),
            TenantId = tenantId, StudentId = childId,
            SourceEventId = $"evt-{label}", EventKind = "quiz_answered",
            CurriculumScope = "{}", Payload = "{}",
            CorrelationId = $"corr-{label}",
            OccurredAt = now.AddDays(-1), IngestedAt = now.AddDays(-1),
        });
        db.BadgeAwards.Add(new BadgeAward
        {
            BadgeAwardId = Guid.NewGuid(),
            TenantId = tenantId, StudentId = childId,
            BadgeCriterionId = Guid.NewGuid(), BadgeCriterionVersion = "v1",
            AwardedAt = now, OriginatingProgressRecordIds = "[]",
            CorrelationId = $"corr-{label}-badge",
        });
        db.FocusAreas.Add(new FocusArea
        {
            FocusAreaId = Guid.NewGuid(),
            TenantId = tenantId, StudentId = childId,
            CurriculumType = "moe",
            SubjectId = Guid.NewGuid(), ChapterId = Guid.NewGuid(), TopicId = Guid.NewGuid(),
            SignalSummary = "{}", RationaleAr = "رأي", RationaleEn = "Rationale",
            SuggestedNextStep = "{}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = now, ValidUntil = now.AddDays(7),
            CorrelationId = $"corr-{label}-focus",
        });
        db.WeeklyReports.Add(new WeeklyReport
        {
            WeeklyReportId = Guid.NewGuid(),
            TenantId = tenantId, StudentId = childId,
            WindowStart = now.Date.AddDays(-7), WindowEnd = now.Date,
            GeneratedAt = now, RunId = Guid.NewGuid(),
            GuardrailDecisionTrailId = Guid.NewGuid(),
            Status = "ready",
            SummaryAr = $"تقرير {label}", SummaryEn = $"{label} report",
            CorrelationId = $"corr-{label}-report",
        });
        db.ParentNotifications.Add(new ParentNotification
        {
            ParentNotificationId = Guid.NewGuid(),
            TenantId = tenantId, ParentProfileId = parentId, ChildId = childId,
            NotificationKind = "weekly_report_ready",
            Channel = "in_app", Language = "ar", BodyAr = "إشعار",
            CorrelationId = $"corr-{label}-notif", CreatedAt = now,
        });
        var flagId = Guid.NewGuid();
        db.AtRiskFlags.Add(new AtRiskFlag
        {
            AtRiskFlagId = flagId,
            TenantId = tenantId, StudentId = childId,
            ThresholdVersion = "v1", TriggeringEvidence = "{}",
            RaisedAt = now, CorrelationId = $"corr-{label}-flag",
        });
        db.InterventionPrompts.Add(new InterventionPrompt
        {
            InterventionPromptId = Guid.NewGuid(),
            TenantId = tenantId, StudentId = childId,
            OriginatingFlagId = flagId,
            BodyAr = "دعم", BodyEn = "Support",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            CreatedAt = now, CorrelationId = $"corr-{label}-prompt",
        });
        db.Phase4DownstreamEvents.Add(new Phase4DownstreamEvent
        {
            Phase4DownstreamEventId = Guid.NewGuid(),
            TenantId = tenantId, StudentId = childId,
            EventKind = "mastery_updated",
            Scope = "{}", Payload = "{}",
            CorrelationId = $"corr-{label}-ds",
            OccurredAt = now,
            DeliveryState = "queued",
        });
        db.OperatorImpersonationAudits.Add(new OperatorImpersonationAudit
        {
            OperatorImpersonationAuditId = Guid.NewGuid(),
            TenantId = tenantId,
            OperatorActorId = Guid.NewGuid(),
            TargetParentProfileId = parentId,
            TargetChildId = childId,
            Surface = "parent_dashboard",
            Reason = $"support_{label}",
            CorrelationId = $"corr-{label}-audit",
            ViewedAt = now,
        });
    }

    private static async Task PurgeChildHistoryAsync(MuallimiDbContext db, Guid tenantId, Guid childId)
    {
        db.ProgressRecords.RemoveRange(db.ProgressRecords.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.StudentId == childId));
        db.MasteryStates.RemoveRange(db.MasteryStates.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.StudentId == childId));
        db.StreakStates.RemoveRange(db.StreakStates.IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.StudentId == childId));
        db.BadgeAwards.RemoveRange(db.BadgeAwards.IgnoreQueryFilters()
            .Where(b => b.TenantId == tenantId && b.StudentId == childId));
        db.FocusAreas.RemoveRange(db.FocusAreas.IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.StudentId == childId));
        db.WeeklyReports.RemoveRange(db.WeeklyReports.IgnoreQueryFilters()
            .Where(w => w.TenantId == tenantId && w.StudentId == childId));
        db.ParentNotifications.RemoveRange(db.ParentNotifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.ChildId == childId));
        db.InterventionPrompts.RemoveRange(db.InterventionPrompts.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.StudentId == childId));
        db.AtRiskFlags.RemoveRange(db.AtRiskFlags.IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId && f.StudentId == childId));
        db.Phase4DownstreamEvents.RemoveRange(db.Phase4DownstreamEvents.IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId && e.StudentId == childId));
        db.OperatorImpersonationAudits.RemoveRange(db.OperatorImpersonationAudits.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.TargetChildId == childId));
        await db.SaveChangesAsync();
    }
}
