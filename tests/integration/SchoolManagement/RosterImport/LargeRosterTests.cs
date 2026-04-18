using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Muallimi.Api.SchoolManagement.DownstreamEvents;
using Muallimi.Api.SchoolManagement.RosterImport;
using Muallimi.Api.Tests.Integration.SchoolManagement;
using Muallimi.Domain.SchoolManagement;
using Xunit;

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement.RosterImport;

/// <summary>
/// T208 (Polish) — Large-file roster import.
///
/// Verifies the worker can ingest 500+ rows without timing out and that
/// partial-failure semantics hold: if a subset of rows is invalid, the
/// valid rows are persisted, invalid rows land in the error report, and
/// the import row itself completes (not <c>failed</c>) so the admin sees
/// a partial success. This is the readiness-gate check described in
/// FR-011 (large-import resilience).
///
/// Budget: 30 seconds on the local InMemory stack. Any slower is a red
/// flag — production will run against PostgreSQL which is orders of
/// magnitude faster, but the local walkthrough has to stay snappy so
/// developers iterate in under a minute.
/// </summary>
public class LargeRosterTests
{
    private const int BudgetMilliseconds = 30_000;

    [Fact]
    public async Task Imports_600_Valid_Rows_Within_Budget()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId, seatLimit: 1000);

        var files = new InMemoryRosterFileStore();
        var content = BuildRoster(600, includeInvalid: false);

        var sw = Stopwatch.StartNew();
        var outcome = await RunImport(db, tenantId, schoolTenantId, files, content, "corr-large");
        sw.Stop();

        Assert.Equal(600, outcome.TotalRowCount);
        Assert.Equal(600, outcome.SuccessCount);
        Assert.Equal(0, outcome.ErrorCount);
        Assert.Equal("completed", outcome.Status);
        Assert.True(
            sw.ElapsedMilliseconds < BudgetMilliseconds,
            $"Large roster import took {sw.ElapsedMilliseconds}ms, budget is {BudgetMilliseconds}ms.");

        var students = await db.StudentProfiles
            .IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == tenantId);
        Assert.Equal(600, students);
    }

    [Fact]
    public async Task Partial_Failure_Preserves_Valid_Rows_And_Reports_Errors()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId, seatLimit: 1000);

        var files = new InMemoryRosterFileStore();
        // 500 valid rows interleaved with 20 rows whose grade is out of
        // the school's range (1..12) — those must be rejected while the
        // remaining 500 survive.
        var content = BuildRoster(500, includeInvalid: true);

        var outcome = await RunImport(db, tenantId, schoolTenantId, files, content, "corr-partial");

        Assert.Equal(520, outcome.TotalRowCount);
        Assert.Equal(500, outcome.SuccessCount);
        Assert.Equal(20, outcome.ErrorCount);
        Assert.Equal("completed", outcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(outcome.ErrorReportBlobKey));

        var students = await db.StudentProfiles
            .IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == tenantId);
        Assert.Equal(500, students);
    }

    [Fact]
    public async Task SeatLimit_Trims_Excess_Rows_Even_For_Large_File()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId, seatLimit: 300);

        var files = new InMemoryRosterFileStore();
        var content = BuildRoster(500, includeInvalid: false);

        var outcome = await RunImport(db, tenantId, schoolTenantId, files, content, "corr-trim");

        Assert.True(outcome.SeatLimitReached);
        Assert.Equal(300, outcome.SuccessCount);
        Assert.Equal(200, outcome.SkipCount);
        Assert.Equal(500, outcome.TotalRowCount);

        var license = await db.SchoolLicenses
            .IgnoreQueryFilters()
            .FirstAsync(l => l.SchoolTenantId == schoolTenantId);
        Assert.Equal(300, license.SeatsUsed);
    }

    private static byte[] BuildRoster(int validCount, bool includeInvalid)
    {
        var sb = new StringBuilder();
        sb.Append("student_name_ar,student_name_en,grade,parent_name,parent_email\n");
        for (var i = 0; i < validCount; i++)
        {
            sb.Append($"طالب{i},Student{i},7,ولي{i},parent{i}@example.test\n");
        }
        if (includeInvalid)
        {
            for (var i = 0; i < 20; i++)
            {
                sb.Append($"طالب-غ-{i},BadStudent{i},99,ولي{i},badparent{i}@example.test\n");
            }
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static async Task<RosterImportProcessOutcome> RunImport(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        Guid tenantId, Guid schoolTenantId,
        InMemoryRosterFileStore files, byte[] content, string correlationId)
    {
        var rosterImportId = Guid.NewGuid();
        var blobKey = $"roster-imports/{rosterImportId}.csv";
        await files.WriteAsync(blobKey, content);

        db.RosterImports.Add(new Muallimi.Domain.SchoolManagement.RosterImport
        {
            RosterImportId = rosterImportId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UploadedByAdminId = Guid.NewGuid(),
            SourceFileBlobKey = blobKey,
            OriginalFileName = "large-roster.csv",
            Status = "uploaded",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var worker = new RosterImportWorker(
            new RosterImportRepository(db),
            files,
            new RosterFileParser(),
            new RosterRowValidator(),
            new StudentProfileLinker(db),
            new Phase5DownstreamEventOutbox(db),
            db);
        return await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blobKey, correlationId),
            CancellationToken.None);
    }

    private static void SeedSchool(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        Guid tenantId, Guid schoolTenantId, int seatLimit)
    {
        var now = DateTime.UtcNow;
        db.SchoolTenants.Add(new SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = "مدرسة الاختبار الكبير",
            SchoolNameEn = "Large Test School",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SchoolLicenses.Add(new SchoolLicense
        {
            SchoolLicenseId = Guid.NewGuid(),
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            PlanTier = "trial",
            SeatLimit = seatLimit,
            SeatsUsed = 0,
            FeatureGates = "{}",
            SubscriptionStart = now.AddDays(-1),
            SubscriptionEnd = now.AddDays(30),
            IsTrial = true,
            SeatWarningThreshold = 90,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }
}
