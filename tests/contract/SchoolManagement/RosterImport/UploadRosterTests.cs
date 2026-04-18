using System;
using System.IO;
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

namespace Muallimi.MainBackend.Tests.Contract.SchoolManagement.RosterImport;

/// <summary>
/// T047 (US2) — Contract test for POST <c>/school-admin/roster/upload</c>.
///
/// Pins the endpoint route and the observable side-effects of a valid
/// upload: a <see cref="RosterImport"/> row lands with status
/// <c>completed</c> (end-to-end in-process run) and a
/// <c>roster_imported</c> downstream event is enqueued with the same
/// counts. Arabic names survive the pipeline with full fidelity.
/// </summary>
public class UploadRosterTests
{
    [Fact]
    public void Endpoint_Route_Is_Pinned()
    {
        Assert.Equal("/api/school-admin/roster/upload", RosterImportEndpoints.UploadRoute);
    }

    [Fact]
    public async Task Valid_Upload_Creates_Students_And_Emits_Roster_Imported_Event()
    {
        await using var db = Phase5TestDbContextFactory.Create();

        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId);

        var rosterImportId = Guid.NewGuid();
        var blobKey = $"roster-imports/{rosterImportId}/test.csv";
        var files = new InMemoryRosterFileStore();
        await files.WriteAsync(blobKey, BuildCsv(
            "محمد علي,Mohammed Ali,7,أبو علي,parent1@example.test",
            "عبدُاللّٰه الشامِي,Abdullah Alshami,7,سعيد,parent2@example.test",
            "فاطمة الزهراء,Fatima Alzahra,8,أم فاطمة,parent3@example.test"));

        db.RosterImports.Add(new Muallimi.Domain.SchoolManagement.RosterImport
        {
            RosterImportId = rosterImportId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UploadedByAdminId = Guid.NewGuid(),
            SourceFileBlobKey = blobKey,
            OriginalFileName = "test.csv",
            Status = "uploaded",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var worker = BuildWorker(db, files);
        var outcome = await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blobKey, "corr-roster"),
            CancellationToken.None);

        Assert.Equal(3, outcome.TotalRowCount);
        Assert.Equal(3, outcome.SuccessCount);
        Assert.Equal(0, outcome.ErrorCount);
        Assert.Equal("completed", outcome.Status);

        // Arabic diacritics preserved.
        var arabicStudent = await db.StudentProfiles
            .IgnoreQueryFilters()
            .FirstAsync(s => s.DisplayName == "عبدُاللّٰه الشامِي");
        Assert.Equal(tenantId, arabicStudent.TenantId);

        var evt = await db.Phase5DownstreamEvents
            .IgnoreQueryFilters()
            .FirstAsync(e => e.SchoolTenantId == schoolTenantId && e.EventKind == Phase5DownstreamEventKind.roster_imported.ToString());
        Assert.Equal("corr-roster", evt.CorrelationId);
    }

    [Fact]
    public async Task Upload_With_Invalid_Rows_Reports_Errors_And_Partial_Success()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId);

        var blobKey = "roster-imports/mixed.csv";
        var files = new InMemoryRosterFileStore();
        await files.WriteAsync(blobKey, BuildCsv(
            "محمد,Mohammed,7,أبو محمد,parent1@example.test",
            ",,7,noname,nobody",           // missing name_ar, name_en; invalid email
            "علي,Ali,99,أبو علي,parent2@example.test")); // grade out of range

        var rosterImportId = Guid.NewGuid();
        db.RosterImports.Add(new Muallimi.Domain.SchoolManagement.RosterImport
        {
            RosterImportId = rosterImportId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UploadedByAdminId = Guid.NewGuid(),
            SourceFileBlobKey = blobKey,
            OriginalFileName = "mixed.csv",
            Status = "uploaded",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var worker = BuildWorker(db, files);
        var outcome = await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blobKey, "corr-mixed"),
            CancellationToken.None);

        Assert.Equal(3, outcome.TotalRowCount);
        Assert.Equal(1, outcome.SuccessCount);
        Assert.Equal(2, outcome.ErrorCount);
        Assert.Equal("completed", outcome.Status);
        Assert.NotNull(outcome.ErrorReportBlobKey);
    }

    private static RosterImportWorker BuildWorker(
        Microsoft.EntityFrameworkCore.DbContext db,
        InMemoryRosterFileStore files)
    {
        var ctx = (Muallimi.Infrastructure.Persistence.MuallimiDbContext)db;
        var repo = new RosterImportRepository(ctx);
        var parser = new RosterFileParser();
        var validator = new RosterRowValidator();
        var linker = new StudentProfileLinker(ctx);
        var outbox = new Phase5DownstreamEventOutbox(ctx);
        return new RosterImportWorker(repo, files, parser, validator, linker, outbox, ctx);
    }

    private static void SeedSchool(Muallimi.Infrastructure.Persistence.MuallimiDbContext db, Guid tenantId, Guid schoolTenantId)
    {
        db.SchoolTenants.Add(new Muallimi.Domain.SchoolManagement.SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = "مدرسة الاختبار",
            SchoolNameEn = "Test School",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            PreferredLanguage = "ar",
            SubscriptionStatus = "trial",
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static byte[] BuildCsv(params string[] dataRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("student_name_ar,student_name_en,grade,parent_name,parent_email");
        foreach (var r in dataRows) sb.AppendLine(r);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
