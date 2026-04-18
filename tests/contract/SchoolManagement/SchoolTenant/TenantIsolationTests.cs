using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.AdminOnboarding;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.SchoolTenant;

/// <summary>
/// T033 (US1) — Tenant isolation for the Phase 5 school provisioning
/// surface.
///
/// Seeds two independent tenants via the Phase 5 provisioning service and
/// asserts that:
///   • Tenant A's repository reads never surface Tenant B's school tenant;
///   • GetConfigurationAsync(tenantA, schoolB) returns null;
///   • Admin onboarding lookups stay scoped — GetByUserIdentityAsync with
///     Tenant A returns null for Tenant B's administrator row.
/// </summary>
public class TenantIsolationTests
{
    [Fact]
    public async Task School_Tenant_Lookup_Is_Tenant_Scoped()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolTenantRepository(db);
        var outbox = new Phase5DownstreamEventOutbox(db);
        var service = new SchoolTenantProvisioningService(repo, outbox);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schoolA = await service.CreateAsync(NewInput(tenantA, "alpha"), "corr-a", CancellationToken.None);
        var schoolB = await service.CreateAsync(NewInput(tenantB, "beta"), "corr-b", CancellationToken.None);

        Assert.NotEqual(schoolA.SchoolTenantId, schoolB.SchoolTenantId);

        // Tenant A cannot see Tenant B's school record.
        var crossLookup = await service.GetConfigurationAsync(tenantA, schoolB.SchoolTenantId, CancellationToken.None);
        Assert.Null(crossLookup);

        // Tenant B still sees its own record.
        var selfLookup = await service.GetConfigurationAsync(tenantB, schoolB.SchoolTenantId, CancellationToken.None);
        Assert.NotNull(selfLookup);
        Assert.Equal(tenantB, selfLookup!.TenantId);
    }

    [Fact]
    public async Task Update_With_Wrong_Tenant_Refuses_To_Mutate()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolTenantRepository(db);
        var outbox = new Phase5DownstreamEventOutbox(db);
        var service = new SchoolTenantProvisioningService(repo, outbox);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schoolB = await service.CreateAsync(NewInput(tenantB, "beta"), "corr-b", CancellationToken.None);

        var result = await service.UpdateConfigurationAsync(
            tenantA,
            schoolB.SchoolTenantId,
            new SchoolTenantUpdateInput(null, null, "en"),
            CancellationToken.None);
        Assert.Null(result);

        var fresh = await db.SchoolTenants.IgnoreQueryFilters()
            .FirstAsync(s => s.SchoolTenantId == schoolB.SchoolTenantId);
        Assert.Equal("ar", fresh.PreferredLanguage);
    }

    [Fact]
    public async Task Admin_Lookup_By_User_Identity_Is_Tenant_Scoped()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var repo = new SchoolAdminRepository(db);
        var service = new AdminOnboardingService(repo);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schoolA = Guid.NewGuid();
        var schoolB = Guid.NewGuid();
        var sharedUserId = Guid.NewGuid();

        var adminA = await service.InviteAsync(
            new AdminInviteInput(tenantA, schoolA, "a@example.test", "م", "A"),
            CancellationToken.None);
        await service.CompleteOnboardingAsync(
            new AdminCompleteInput(adminA.SchoolAdminId, sharedUserId, true),
            CancellationToken.None);

        var adminB = await service.InviteAsync(
            new AdminInviteInput(tenantB, schoolB, "b@example.test", "م", "B"),
            CancellationToken.None);
        await service.CompleteOnboardingAsync(
            new AdminCompleteInput(adminB.SchoolAdminId, sharedUserId, true),
            CancellationToken.None);

        var foundForA = await repo.GetByUserIdentityAsync(tenantA, schoolA, sharedUserId, CancellationToken.None);
        var foundForB = await repo.GetByUserIdentityAsync(tenantB, schoolB, sharedUserId, CancellationToken.None);
        var crossA = await repo.GetByUserIdentityAsync(tenantA, schoolB, sharedUserId, CancellationToken.None);

        Assert.NotNull(foundForA);
        Assert.NotNull(foundForB);
        Assert.NotEqual(foundForA!.SchoolAdminId, foundForB!.SchoolAdminId);
        Assert.Null(crossA);
    }

    private static SchoolTenantCreateInput NewInput(Guid tenantId, string label) =>
        new(
            SchoolNameAr: $"مدرسة {label}",
            SchoolNameEn: $"School {label}",
            CurriculumType: "moe",
            GradeRangeStart: 1,
            GradeRangeEnd: 12,
            SubjectBindings: new List<string> { "math" },
            AcademicCalendar: new { },
            PreferredLanguage: "ar",
            CreatedByOperatorId: Guid.NewGuid(),
            TenantId: tenantId);
}
