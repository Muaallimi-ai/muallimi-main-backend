using System;
using System.Linq;
using System.Threading.Tasks;
using Muallimi.Api.Parents.ParentDashboard;
using Muallimi.Api.StudentProgressSurface;
using Muallimi.Api.Tests.Integration.Engagement;
using Muallimi.Domain.Engagement;
using Muallimi.Domain.Parents;
using Muallimi.Domain.StudentExperience;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.Engagement;

/// <summary>
/// T064 (US2) — Tenant isolation integration test for the parent
/// dashboard, including sibling (one parent → two children) and
/// co-parent (two parents → one child) shapes.
///
/// Seeds tenants alpha + beta via <see cref="TenantIsolationHarness"/>
/// and layers the sibling + co-parent shapes on top. Asserts:
///   - A parent only sees their own children in
///     <see cref="IParentDashboardService.ListChildrenAsync"/>.
///   - A co-parent in the same tenant does not see the other parent's
///     children unless the co-parent also holds an active ChildLink.
///   - A revoked ChildLink (effective_end in the past) drops off the
///     selector in the next render.
///   - <see cref="IChildLinkRepository.GetActiveAsync"/> returns null
///     for a cross-tenant child id so dashboard rendering refuses the
///     request with 404.
/// </summary>
public class ParentDashboardTenantIsolationTests
{
    [Fact]
    public async Task ListChildren_Only_Returns_Rows_For_The_Authenticated_Parent_And_Tenant()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        db.StudentProfiles.Add(new StudentProfile
        {
            Id = TenantIsolationHarness.SharedStudentIdAlpha,
            TenantId = TenantIsolationHarness.TenantAlpha,
            DisplayName = "طالب ألفا",
            CurriculumType = "moe",
            Grade = "grade_6",
            PreferredLanguage = "ar",
            PlanTier = "free",
        });
        db.StudentProfiles.Add(new StudentProfile
        {
            Id = TenantIsolationHarness.SharedStudentIdBeta,
            TenantId = TenantIsolationHarness.TenantBeta,
            DisplayName = "طالب بيتا",
            CurriculumType = "language_school",
            Grade = "grade_7",
            PreferredLanguage = "en",
            PlanTier = "family",
        });
        await db.SaveChangesAsync();

        var service = new ParentDashboardService(db, new DefaultCurriculumLabelResolver());

        var alpha = await service.ListChildrenAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedParentIdAlpha);
        Assert.Single(alpha);
        Assert.Equal(TenantIsolationHarness.SharedStudentIdAlpha, alpha[0].ChildId);
        Assert.Equal("moe", alpha[0].CurriculumType);

        var beta = await service.ListChildrenAsync(
            TenantIsolationHarness.TenantBeta,
            TenantIsolationHarness.SharedParentIdBeta);
        Assert.Single(beta);
        Assert.Equal(TenantIsolationHarness.SharedStudentIdBeta, beta[0].ChildId);

        // Cross-tenant lookup: alpha parent querying under beta tenant returns empty.
        var crossTenant = await service.ListChildrenAsync(
            TenantIsolationHarness.TenantBeta,
            TenantIsolationHarness.SharedParentIdAlpha);
        Assert.Empty(crossTenant);
    }

    [Fact]
    public async Task ListChildren_Supports_Sibling_Shape_Under_One_Parent()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var childA = Guid.NewGuid();
        var childB = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = parentId,
            TenantId = tenantId,
            IdentityId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId,
            ParentProfileId = parentId,
            StudentId = childA,
            Role = "guardian",
            EffectiveStart = DateTime.UtcNow.Date.AddDays(-10),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId,
            ParentProfileId = parentId,
            StudentId = childB,
            Role = "guardian",
            EffectiveStart = DateTime.UtcNow.Date.AddDays(-5),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.StudentProfiles.Add(new StudentProfile
        {
            Id = childA, TenantId = tenantId, DisplayName = "الابن الأكبر",
            CurriculumType = "moe", Grade = "grade_6", PreferredLanguage = "ar",
        });
        db.StudentProfiles.Add(new StudentProfile
        {
            Id = childB, TenantId = tenantId, DisplayName = "الابنة الصغرى",
            CurriculumType = "moe", Grade = "grade_3", PreferredLanguage = "ar",
        });
        await db.SaveChangesAsync();

        var service = new ParentDashboardService(db, new DefaultCurriculumLabelResolver());
        var children = await service.ListChildrenAsync(tenantId, parentId);
        Assert.Equal(2, children.Length);
        Assert.Contains(children, c => c.ChildId == childA);
        Assert.Contains(children, c => c.ChildId == childB);
    }

    [Fact]
    public async Task ListChildren_Supports_CoParent_Shape_And_Respects_Active_Link()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var guardian = Guid.NewGuid();
        var coParent = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = guardian, TenantId = tenantId, IdentityId = Guid.NewGuid(),
            CreatedAt = now, UpdatedAt = now,
        });
        db.ParentProfiles.Add(new ParentProfile
        {
            ParentProfileId = coParent, TenantId = tenantId, IdentityId = Guid.NewGuid(),
            CreatedAt = now, UpdatedAt = now,
        });
        db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId, ParentProfileId = guardian, StudentId = childId,
            Role = "guardian", EffectiveStart = DateTime.UtcNow.Date.AddDays(-10),
            CreatedAt = now, UpdatedAt = now,
        });
        db.ChildLinks.Add(new ChildLink
        {
            ChildLinkId = Guid.NewGuid(),
            TenantId = tenantId, ParentProfileId = coParent, StudentId = childId,
            Role = "co_parent", EffectiveStart = DateTime.UtcNow.Date.AddDays(-5),
            CreatedAt = now, UpdatedAt = now,
        });
        db.StudentProfiles.Add(new StudentProfile
        {
            Id = childId, TenantId = tenantId, DisplayName = "طفل مشترك",
            CurriculumType = "moe", Grade = "grade_6", PreferredLanguage = "ar",
        });
        await db.SaveChangesAsync();

        var service = new ParentDashboardService(db, new DefaultCurriculumLabelResolver());
        Assert.Single(await service.ListChildrenAsync(tenantId, guardian));
        Assert.Single(await service.ListChildrenAsync(tenantId, coParent));

        // Revoke the co-parent's link — the selector must hide the child immediately.
        var link = db.ChildLinks.Single(l => l.ParentProfileId == coParent && l.StudentId == childId);
        link.EffectiveEnd = DateTime.UtcNow.Date.AddDays(-1);
        await db.SaveChangesAsync();

        Assert.Empty(await service.ListChildrenAsync(tenantId, coParent));
        Assert.Single(await service.ListChildrenAsync(tenantId, guardian));
    }

    [Fact]
    public async Task ChildLinkRepository_Cross_Tenant_Lookup_Returns_Null()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var repo = new ChildLinkRepository(db);

        var crossTenant = await repo.GetActiveAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedParentIdAlpha,
            TenantIsolationHarness.SharedStudentIdBeta);
        Assert.Null(crossTenant);

        var sameTenant = await repo.GetActiveAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedParentIdAlpha,
            TenantIsolationHarness.SharedStudentIdAlpha);
        Assert.NotNull(sameTenant);
    }

    [Fact]
    public async Task Dashboard_Build_Filters_Mastery_Focus_And_Activity_By_Tenant_And_Child()
    {
        await using var db = Phase4TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        db.StudentProfiles.Add(new StudentProfile
        {
            Id = TenantIsolationHarness.SharedStudentIdAlpha,
            TenantId = TenantIsolationHarness.TenantAlpha,
            DisplayName = "طالب ألفا",
            CurriculumType = "moe",
            Grade = "grade_6",
            PreferredLanguage = "ar",
            PlanTier = "family",
        });

        // Seed a focus area for beta only.
        db.FocusAreas.Add(new FocusArea
        {
            FocusAreaId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantBeta,
            StudentId = TenantIsolationHarness.SharedStudentIdBeta,
            CurriculumType = "moe",
            SubjectId = Guid.NewGuid(),
            ChapterId = Guid.NewGuid(),
            TopicId = Guid.NewGuid(),
            SignalSummary = "{}",
            RationaleAr = "ركّز على هذا",
            RationaleEn = "Focus here",
            SuggestedNextStep = "{\"phase3_mode\":\"solve_questions\",\"deep_link\":\"/solve-questions\"}",
            GuardrailDecisionTrailId = Guid.NewGuid(),
            ComputedAt = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(3),
            CorrelationId = Guid.NewGuid().ToString("D"),
        });
        await db.SaveChangesAsync();

        var service = new ParentDashboardService(db, new DefaultCurriculumLabelResolver());
        var payload = await service.BuildDashboardAsync(
            TenantIsolationHarness.TenantAlpha,
            TenantIsolationHarness.SharedParentIdAlpha,
            TenantIsolationHarness.SharedStudentIdAlpha,
            correlationId: "corr-alpha-1");

        Assert.Equal(TenantIsolationHarness.SharedStudentIdAlpha, payload.ChildId);
        Assert.Equal("moe", payload.CurriculumType);
        Assert.Equal("grade_6", payload.Grade);
        Assert.Single(payload.MasteryBySubject); // only alpha's mastery row
        Assert.Empty(payload.FocusAreasThisWeek); // beta's focus area is invisible
        Assert.Single(payload.RecentActivity); // only alpha's session_start record
        Assert.Null(payload.LatestWeeklyReport);
        Assert.Null(payload.AtRiskFlag);
        Assert.True(payload.PlanView.IsReadOnly);
        Assert.Equal("family", payload.PlanView.PlanTier);
        Assert.Equal("corr-alpha-1", payload.CorrelationId);
    }
}
