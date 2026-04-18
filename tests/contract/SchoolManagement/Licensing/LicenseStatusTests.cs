using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.Licensing;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.Licensing;

/// <summary>
/// T182 (US10) — contract test for GET /school-admin/license.
///
/// Pins the route + request shape and drives the service layer end-to-end to
/// confirm that school-admin queries return the expected license projection
/// and that cross-tenant reads stay isolated.
/// </summary>
public class LicenseStatusTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/school-admin/license", LicenseStatusEndpoints.Route);
    }

    [Fact]
    public void HandleGetAsync_Is_Exposed_For_Testability()
    {
        var method = typeof(LicenseStatusEndpoints).GetMethod("HandleGetAsync",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
    }

    [Fact]
    public async Task SchoolAdmin_Get_Returns_Seeded_License_For_Own_Tenant()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaSeatLimit: 50, alphaSeatsUsed: 20);

        var service = BuildService(db);

        var license = await service.GetForSchoolAdminAsync(
            LicensingHarness.TenantAlpha,
            LicensingHarness.SchoolAlpha);

        Assert.NotNull(license);
        Assert.Equal(50, license!.SeatLimit);
        Assert.Equal(20, license.SeatsUsed);
        Assert.Equal("starter", license.PlanTier);
        Assert.True(license.IsTrial);
    }

    [Fact]
    public async Task SchoolAdmin_Get_Does_Not_Leak_Other_Tenants_License()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync();

        var service = BuildService(db);

        // Alpha admin asking for Beta's school must get nothing.
        var leaked = await service.GetForSchoolAdminAsync(
            LicensingHarness.TenantAlpha,
            LicensingHarness.SchoolBeta);

        Assert.Null(leaked);
    }

    private static LicenseManagementService BuildService(Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        var repo = new SchoolLicenseRepository(db);
        var outbox = new Phase5DownstreamEventOutbox(db);
        var schoolRepo = new SchoolTenantRepository(db);
        var notifier = new SeatWarningNotifier(Microsoft.Extensions.Logging.Abstractions.NullLogger<SeatWarningNotifier>.Instance);
        return new LicenseManagementService(repo, outbox, schoolRepo, notifier);
    }
}
