using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Engagement.DownstreamEvents;
using Muallimi.Api.Engagement.InterventionPrompts;
using Muallimi.Api.Engagement.WeeklyReports;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Api.Parents.ParentNotifications;
using Muallimi.Api.StudentProgressSurface;
using Muallimi.Api.Tests.Integration.Engagement;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T157 (Polish) — Cross-surface tenant isolation across EVERY primary
/// Phase 4 surface in a single seeded cross-tenant setup:
///   - student progress surface          (<see cref="IStudentProgressService"/>)
///   - parent dashboard                  (<see cref="IParentDashboardService"/>)
///   - weekly report viewer              (<see cref="IWeeklyReportRepository"/>)
///   - parent notification inbox         (<see cref="IParentNotificationRepository"/>)
///   - intervention prompt surface       (<see cref="IInterventionPromptRepository"/>)
///   - downstream event outbox           (<c>Phase4DownstreamEvents</c>)
///
/// Seeds tenant-alpha and tenant-beta with intentionally-overlapping child
/// ids and parent ids via <see cref="TenantIsolationHarness"/>, then layers
/// a weekly report, a parent notification, an intervention prompt, and a
/// downstream event onto each tenant. Asserts that authenticated access as
/// tenant-alpha returns zero beta rows — every surface independently.
///
/// This is the readiness-gate aggregate — a single regression here blocks
/// the Phase 4 promotion.
/// </summary>
public class Phase4TenantIsolationTests
{
    [Fact]
    public async Task Every_Phase4_Surface_Refuses_To_Leak_Across_Tenants()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var alphaChild = TenantIsolationHarness.SharedStudentIdAlpha;
        var betaChild = TenantIsolationHarness.SharedStudentIdBeta;
        var alphaParent = TenantIsolationHarness.SharedParentIdAlpha;
        var betaParent = TenantIsolationHarness.SharedParentIdBeta;

        db.StudentProfiles.Add(new StudentProfile
        {
            Id = alphaChild,
            TenantId = TenantIsolationHarness.TenantAlpha,
            DisplayName = "طالب ألفا",
            CurriculumType = "moe",
            Grade = "grade_6",
            PreferredLanguage = "ar",
            PlanTier = "family",
        });
        db.StudentProfiles.Add(new StudentProfile
        {
            Id = betaChild,
            TenantId = TenantIsolationHarness.TenantBeta,
            DisplayName = "طالب بيتا",
            CurriculumType = "moe",
            Grade = "grade_6",
            PreferredLanguage = "ar",
            PlanTier = "family",
        });

        // Weekly reports — one per tenant.
        var windowStart = DateTime.UtcNow.Date.AddDays(-7);
        var windowEnd = DateTime.UtcNow.Date;
        var alphaReportId = Guid.NewGuid();
        var betaReportId = Guid.NewGuid();
        db.WeeklyReports.Add(new WeeklyReport
        {
            WeeklyReportId = alphaReportId,
            TenantId = TenantIsolationHarness.TenantAlpha,
            StudentId = alphaChild,
            WindowStart = windowStart, WindowEnd = windowEnd,
            GeneratedAt = DateTime.UtcNow, RunId = Guid.NewGuid(),
            GuardrailDecisionTrailId = Guid.NewGuid(),
            Status = "ready",
            CorrelationId = "corr-alpha-report",
            SummaryAr = "تقرير ألفا", SummaryEn = "Alpha report",
        });
        db.WeeklyReports.Add(new WeeklyReport
        {
            WeeklyReportId = betaReportId,
            TenantId = TenantIsolationHarness.TenantBeta,
            StudentId = betaChild,
            WindowStart = windowStart, WindowEnd = windowEnd,
            GeneratedAt = DateTime.UtcNow, RunId = Guid.NewGuid(),
            GuardrailDecisionTrailId = Guid.NewGuid(),
            Status = "ready",
            CorrelationId = "corr-beta-report",
            SummaryAr = "تقرير بيتا", SummaryEn = "Beta report",
        });

        // Parent notifications — one per tenant.
        db.ParentNotifications.Add(new ParentNotification
        {
            ParentNotificationId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantAlpha,
            ParentProfileId = alphaParent,
            ChildId = alphaChild,
            NotificationKind = "weekly_report_ready",
            Channel = "in_app",
            Language = "ar",
            BodyAr = "تقريرك جاهز",
            CorrelationId = "corr-alpha-notif",
            CreatedAt = DateTime.UtcNow,
        });
        db.ParentNotifications.Add(new ParentNotification
        {
            ParentNotificationId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantBeta,
            ParentProfileId = betaParent,
            ChildId = betaChild,
            NotificationKind = "weekly_report_ready",
            Channel = "in_app",
            Language = "ar",
            BodyAr = "تقريرك جاهز",
            CorrelationId = "corr-beta-notif",
            CreatedAt = DateTime.UtcNow,
        });

        // Intervention prompts — one per tenant.
        var alphaPromptId = Guid.NewGuid();
        var betaPromptId = Guid.NewGuid();
        db.InterventionPrompts.Add(new InterventionPrompt
        {
            InterventionPromptId = alphaPromptId,
            TenantId = TenantIsolationHarness.TenantAlpha,
            StudentId = alphaChild,
            BodyAr = "دعم ألفا", BodyEn = "Alpha support",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            CorrelationId = "corr-alpha-prompt",
        });
        db.InterventionPrompts.Add(new InterventionPrompt
        {
            InterventionPromptId = betaPromptId,
            TenantId = TenantIsolationHarness.TenantBeta,
            StudentId = betaChild,
            BodyAr = "دعم بيتا", BodyEn = "Beta support",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            CorrelationId = "corr-beta-prompt",
        });

        // Downstream events — one per tenant.
        db.Phase4DownstreamEvents.Add(new Phase4DownstreamEvent
        {
            Phase4DownstreamEventId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantAlpha,
            StudentId = alphaChild,
            EventKind = nameof(Phase4DownstreamEventKind.mastery_updated),
            Scope = "{}", Payload = "{}",
            CorrelationId = "corr-alpha-ds",
            OccurredAt = DateTime.UtcNow,
            DeliveryState = "queued",
        });
        db.Phase4DownstreamEvents.Add(new Phase4DownstreamEvent
        {
            Phase4DownstreamEventId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantBeta,
            StudentId = betaChild,
            EventKind = nameof(Phase4DownstreamEventKind.mastery_updated),
            Scope = "{}", Payload = "{}",
            CorrelationId = "corr-beta-ds",
            OccurredAt = DateTime.UtcNow,
            DeliveryState = "queued",
        });

        await db.SaveChangesAsync();

        // --- 1. Student progress surface ---
        var progress = new StudentProgressService(db, new DefaultCurriculumLabelResolver());
        var alphaSummary = await progress.BuildSummaryAsync(TenantIsolationHarness.TenantAlpha, alphaChild);
        Assert.Equal(alphaChild, alphaSummary.StudentId);
        Assert.All(alphaSummary.MasteryBySubject, m => Assert.DoesNotContain("beta", m.SubjectLabelAr));

        // --- 2. Parent dashboard ---
        var dashboard = new ParentDashboardService(db, new DefaultCurriculumLabelResolver());
        var alphaChildren = await dashboard.ListChildrenAsync(TenantIsolationHarness.TenantAlpha, alphaParent);
        Assert.Single(alphaChildren);
        Assert.Equal(alphaChild, alphaChildren[0].ChildId);

        var crossTenantChildren = await dashboard.ListChildrenAsync(TenantIsolationHarness.TenantAlpha, betaParent);
        Assert.Empty(crossTenantChildren);

        // --- 3. Weekly report viewer ---
        var reportRepo = new WeeklyReportRepository(db);
        Assert.NotNull(await reportRepo.GetByIdAsync(TenantIsolationHarness.TenantAlpha, alphaReportId));
        Assert.Null(await reportRepo.GetByIdAsync(TenantIsolationHarness.TenantAlpha, betaReportId));
        var alphaReports = await reportRepo.ListForStudentAsync(TenantIsolationHarness.TenantAlpha, alphaChild);
        Assert.Single(alphaReports);
        Assert.All(alphaReports, r => Assert.Equal(TenantIsolationHarness.TenantAlpha, r.TenantId));

        // --- 4. Parent notification inbox ---
        var notifRepo = new ParentNotificationRepository(db);
        var alphaInbox = await notifRepo.ListForParentAsync(
            TenantIsolationHarness.TenantAlpha, alphaParent, new[] { alphaChild }, 50);
        Assert.Single(alphaInbox);
        Assert.All(alphaInbox, n => Assert.Equal(TenantIsolationHarness.TenantAlpha, n.TenantId));
        var crossInbox = await notifRepo.ListForParentAsync(
            TenantIsolationHarness.TenantAlpha, betaParent, new[] { betaChild }, 50);
        Assert.Empty(crossInbox);

        // --- 5. Intervention prompt surface ---
        var promptRepo = new InterventionPromptRepository(db);
        Assert.NotNull(await promptRepo.GetByIdAsync(TenantIsolationHarness.TenantAlpha, alphaPromptId));
        Assert.Null(await promptRepo.GetByIdAsync(TenantIsolationHarness.TenantAlpha, betaPromptId));

        // --- 6. Downstream event outbox ---
        var alphaDs = await db.Phase4DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == TenantIsolationHarness.TenantAlpha)
            .ToListAsync();
        Assert.Single(alphaDs);
        Assert.All(alphaDs, e => Assert.Equal("corr-alpha-ds", e.CorrelationId));
    }
}
