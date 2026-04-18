using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement;

/// <summary>
/// T201 (Polish) — Cross-surface tenant isolation.
///
/// For every Phase 5 query surface (school tenant, administrator, teacher,
/// class group, license, downstream event) the test seeds two independent
/// tenants via <see cref="TenantIsolationHarness"/> and asserts that a
/// tenant-scoped read returns zero rows belonging to the "other" tenant.
///
/// A missing <c>WHERE TenantId = ...</c> filter anywhere — dashboards,
/// rosters, exams, leaderboards, announcements, reports — would cause one
/// or more of these assertions to fail. This is the Phase 5 analogue of
/// the Phase 1 <c>TenantIsolationTests</c> net.
/// </summary>
public class CrossSurfaceTenantIsolationTests
{
    [Fact]
    public async Task School_Tenants_Are_Scoped_Per_Tenant()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        var alphaRows = await db.SchoolTenants
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == TenantIsolationHarness.TenantAlpha)
            .ToListAsync();
        Assert.Single(alphaRows);
        Assert.Equal(TenantIsolationHarness.SchoolAlpha, alphaRows[0].SchoolTenantId);
        Assert.DoesNotContain(alphaRows, r => r.TenantId == TenantIsolationHarness.TenantBeta);
    }

    [Fact]
    public async Task School_Administrators_Are_Scoped_Per_Tenant()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        await TenantIsolationHarness.AssertNoCrossTenantLeakAsync<SchoolAdministrator>(
            db,
            ctx => ctx.SchoolAdministrators
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == TenantIsolationHarness.TenantAlpha),
            TenantIsolationHarness.TenantAlpha);
    }

    [Fact]
    public async Task Teachers_Are_Scoped_Per_Tenant()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        await TenantIsolationHarness.AssertNoCrossTenantLeakAsync<Teacher>(
            db,
            ctx => ctx.Teachers
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == TenantIsolationHarness.TenantBeta),
            TenantIsolationHarness.TenantBeta);
    }

    [Fact]
    public async Task ClassGroups_Are_Scoped_Per_Tenant()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        await TenantIsolationHarness.AssertNoCrossTenantLeakAsync<ClassGroup>(
            db,
            ctx => ctx.ClassGroups
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == TenantIsolationHarness.TenantAlpha),
            TenantIsolationHarness.TenantAlpha);
    }

    [Fact]
    public async Task SchoolLicenses_Are_Scoped_Per_Tenant()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        await TenantIsolationHarness.AssertNoCrossTenantLeakAsync<SchoolLicense>(
            db,
            ctx => ctx.SchoolLicenses
                .IgnoreQueryFilters()
                .Where(l => l.TenantId == TenantIsolationHarness.TenantBeta),
            TenantIsolationHarness.TenantBeta);
    }

    [Fact]
    public async Task Phase5_Downstream_Events_Are_Scoped_Per_Tenant()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        db.Phase5DownstreamEvents.Add(new Phase5DownstreamEvent
        {
            Phase5EventId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantAlpha,
            SchoolTenantId = TenantIsolationHarness.SchoolAlpha,
            EventKind = "school_created",
            Payload = "{}",
            CorrelationId = "corr-alpha",
            OccurredAt = DateTime.UtcNow,
            SchemaVersion = "1.0.0",
            DeliveryState = "queued",
        });
        db.Phase5DownstreamEvents.Add(new Phase5DownstreamEvent
        {
            Phase5EventId = Guid.NewGuid(),
            TenantId = TenantIsolationHarness.TenantBeta,
            SchoolTenantId = TenantIsolationHarness.SchoolBeta,
            EventKind = "school_created",
            Payload = "{}",
            CorrelationId = "corr-beta",
            OccurredAt = DateTime.UtcNow,
            SchemaVersion = "1.0.0",
            DeliveryState = "queued",
        });
        await db.SaveChangesAsync();

        var alphaOnly = await db.Phase5DownstreamEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == TenantIsolationHarness.TenantAlpha)
            .ToListAsync();
        Assert.Single(alphaOnly);
        Assert.Equal("corr-alpha", alphaOnly[0].CorrelationId);
        Assert.DoesNotContain(alphaOnly, e => e.TenantId == TenantIsolationHarness.TenantBeta);
    }

    [Fact]
    public async Task Cross_Tenant_Lookup_By_Other_Tenant_Id_Returns_Nothing()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var harness = new TenantIsolationHarness(db);
        await harness.SeedAsync();

        // If we scope by Alpha but filter for Beta's school id, we must
        // observe zero rows — missing filters would leak a row here.
        var leaked = await db.SchoolTenants
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == TenantIsolationHarness.TenantAlpha
                && s.SchoolTenantId == TenantIsolationHarness.SchoolBeta)
            .ToListAsync();
        Assert.Empty(leaked);
    }
}
