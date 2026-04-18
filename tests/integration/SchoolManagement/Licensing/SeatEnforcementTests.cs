using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.Licensing;
using Muallimi.Api.SchoolManagement.SchoolTenantProvisioning;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement.Licensing;

/// <summary>
/// T184 (US10) — integration test for seat-limit enforcement.
///
/// IncrementSeatsUsedAsync is the atomic seat-consumption primitive the
/// roster-import worker + manual admin flows call. This test pins three
/// outcomes:
///   1. Increment below the limit → Allowed, seats advanced, outbox event.
///   2. Increment past the limit → !Allowed, seats unchanged.
///   3. Warning threshold tripped at ≥90% → WarningTripped=true.
/// </summary>
public class SeatEnforcementTests
{
    [Fact]
    public async Task Increment_Below_Limit_Succeeds_And_Advances_Seats_Used()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaSeatLimit: 50, alphaSeatsUsed: 10);

        var service = BuildService(db);

        var result = await service.IncrementSeatsUsedAsync(
            LicensingHarness.SchoolAlpha,
            delta: 20,
            correlationId: Guid.NewGuid().ToString("D"));

        Assert.True(result.Allowed);
        Assert.Equal(30, result.SeatsUsed);
        Assert.Equal(50, result.SeatLimit);
        Assert.False(result.LimitReached);
        Assert.False(result.WarningTripped);

        var persisted = await db.SchoolLicenses
            .IgnoreQueryFilters()
            .FirstAsync(l => l.SchoolTenantId == LicensingHarness.SchoolAlpha);
        Assert.Equal(30, persisted.SeatsUsed);

        var outbox = db.Phase5DownstreamEvents.Local.ToList();
        Assert.Contains(outbox, e =>
            e.EventKind == nameof(Phase5DownstreamEventKind.license_updated) &&
            e.Payload.Contains("seats_incremented"));
    }

    [Fact]
    public async Task Increment_Past_Limit_Refuses_And_Keeps_Seats_Unchanged()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaSeatLimit: 50, alphaSeatsUsed: 48);

        var service = BuildService(db);

        var result = await service.IncrementSeatsUsedAsync(
            LicensingHarness.SchoolAlpha,
            delta: 5,
            correlationId: Guid.NewGuid().ToString("D"));

        Assert.False(result.Allowed);
        Assert.True(result.LimitReached);
        Assert.Equal(48, result.SeatsUsed);

        var persisted = await db.SchoolLicenses
            .IgnoreQueryFilters()
            .FirstAsync(l => l.SchoolTenantId == LicensingHarness.SchoolAlpha);
        Assert.Equal(48, persisted.SeatsUsed);
    }

    [Fact]
    public async Task Increment_Crossing_Warning_Threshold_Trips_Warning_Flag()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaSeatLimit: 50, alphaSeatsUsed: 40);

        var service = BuildService(db);

        // 40 → 46 = 92% of 50 → warning trips at the 90% threshold.
        var result = await service.IncrementSeatsUsedAsync(
            LicensingHarness.SchoolAlpha,
            delta: 6,
            correlationId: Guid.NewGuid().ToString("D"));

        Assert.True(result.Allowed);
        Assert.Equal(46, result.SeatsUsed);
        Assert.True(result.WarningTripped);
    }

    [Fact]
    public async Task Negative_Delta_Throws_Argument_Exception()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaSeatLimit: 50, alphaSeatsUsed: 10);

        var service = BuildService(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.IncrementSeatsUsedAsync(
                LicensingHarness.SchoolAlpha,
                delta: 0,
                correlationId: Guid.NewGuid().ToString("D")));
    }

    private static LicenseManagementService BuildService(Muallimi.Infrastructure.Persistence.MuallimiDbContext db)
    {
        var repo = new SchoolLicenseRepository(db);
        var outbox = new Phase5DownstreamEventOutbox(db);
        var schoolRepo = new SchoolTenantRepository(db);
        var notifier = new SeatWarningNotifier(NullLogger<SeatWarningNotifier>.Instance);
        return new LicenseManagementService(repo, outbox, schoolRepo, notifier);
    }
}
