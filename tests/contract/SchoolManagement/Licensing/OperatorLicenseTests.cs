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
/// T183 (US10) — contract test for operator license endpoints.
///
/// Pins routes, request shapes, and verifies create / update / list /
/// extend-trial drive the underlying service correctly and emit the
/// expected license_updated downstream event.
/// </summary>
public class OperatorLicenseTests
{
    [Fact]
    public void Endpoint_Routes_Are_Pinned()
    {
        Assert.Equal("/api/operator/schools/{schoolId:guid}/license", OperatorLicenseEndpoints.SingleRoute);
        Assert.Equal("/api/operator/schools/{schoolId:guid}/license/extend-trial", OperatorLicenseEndpoints.ExtendTrialRoute);
        Assert.Equal("/api/operator/licenses", OperatorLicenseEndpoints.ListRoute);
    }

    [Fact]
    public void Put_Request_Shape_Matches_Contract()
    {
        var props = typeof(OperatorLicenseEndpoints.PutRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();
        Assert.Contains("plan_tier", props);
        Assert.Contains("seat_limit", props);
        Assert.Contains("feature_gates", props);
        Assert.Contains("subscription_start", props);
        Assert.Contains("subscription_end", props);
        Assert.Contains("is_trial", props);
    }

    [Fact]
    public async Task Create_Via_Service_Emits_License_Updated_Outbox_Event()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        // Seed tenant only (no license yet).
        var harness = new LicensingHarness(db);
        await harness.SeedAsync();

        // Remove Alpha's license so we can drive a fresh Create.
        var alpha = db.SchoolLicenses.IgnoreQueryFilters()
            .First(l => l.SchoolTenantId == LicensingHarness.SchoolAlpha);
        db.SchoolLicenses.Remove(alpha);
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var correlationId = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow;

        var created = await service.CreateAsync(
            new LicenseCreateInput(
                SchoolTenantId: LicensingHarness.SchoolAlpha,
                TenantId: LicensingHarness.TenantAlpha,
                PlanTier: "growth",
                SeatLimit: 100,
                FeatureGates: "{\"exams\":true}",
                SubscriptionStart: now,
                SubscriptionEnd: now.AddDays(90),
                IsTrial: false),
            correlationId);

        Assert.NotEqual(Guid.Empty, created.SchoolLicenseId);
        Assert.Equal(100, created.SeatLimit);
        Assert.Equal(0, created.SeatsUsed);

        var outboxRows = db.Phase5DownstreamEvents.Local.ToList();
        Assert.Contains(outboxRows, e =>
            e.EventKind == nameof(Phase5DownstreamEventKind.license_updated) &&
            e.TenantId == LicensingHarness.TenantAlpha &&
            e.SchoolTenantId == LicensingHarness.SchoolAlpha);
    }

    [Fact]
    public async Task Update_Changes_Seat_Limit_And_Syncs_School_Subscription_Status()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync();

        var service = BuildService(db);

        var updated = await service.UpdateAsync(
            LicensingHarness.SchoolAlpha,
            new LicenseUpdateInput(
                PlanTier: "growth",
                SeatLimit: 200,
                FeatureGates: "{\"exams\":false,\"announcements\":true}",
                SubscriptionEnd: DateTime.UtcNow.AddDays(120),
                IsTrial: false),
            correlationId: Guid.NewGuid().ToString("D"));

        Assert.NotNull(updated);
        Assert.Equal(200, updated!.SeatLimit);
        Assert.Equal("growth", updated.PlanTier);

        var school = await db.SchoolTenants.IgnoreQueryFilters()
            .FirstAsync(s => s.SchoolTenantId == LicensingHarness.SchoolAlpha);
        Assert.Equal("active", school.SubscriptionStatus);
    }

    [Fact]
    public async Task Extend_Trial_Pushes_Subscription_End_Forward_And_Emits_Event()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync(alphaSubscriptionEnd: DateTime.UtcNow.AddDays(3));

        var service = BuildService(db);
        var newEnd = DateTime.UtcNow.AddDays(60);

        var extended = await service.ExtendTrialAsync(
            LicensingHarness.SchoolAlpha,
            newEnd,
            correlationId: Guid.NewGuid().ToString("D"));

        Assert.NotNull(extended);
        Assert.True(extended!.SubscriptionEnd >= newEnd.ToUniversalTime().AddSeconds(-1));

        var outboxRows = db.Phase5DownstreamEvents.Local.ToList();
        Assert.Contains(outboxRows, e =>
            e.EventKind == nameof(Phase5DownstreamEventKind.license_updated) &&
            e.Payload.Contains("trial_extended"));
    }

    [Fact]
    public async Task List_Returns_Both_Tenants_Licenses_For_Operator()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new LicensingHarness(db);
        await harness.SeedAsync();

        var service = BuildService(db);

        var all = await service.ListForOperatorAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, l => l.SchoolTenantId == LicensingHarness.SchoolAlpha);
        Assert.Contains(all, l => l.SchoolTenantId == LicensingHarness.SchoolBeta);
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
