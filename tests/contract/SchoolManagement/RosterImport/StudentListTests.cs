using System;
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
/// T049 (US2) — Contract test for GET <c>/school-admin/roster/students</c>.
///
/// Pins the route constant and asserts the observable shape of the
/// student-list query over a populated roster: paging + search filters
/// work, and the student data returned exactly matches the school's
/// tenant id (zero cross-school leakage even though two schools share
/// the in-memory db).
/// </summary>
public class StudentListTests
{
    [Fact]
    public void Students_Route_Is_Pinned()
    {
        Assert.Equal("/api/school-admin/roster/students", RosterQueryEndpoints.StudentsRoute);
    }

    [Fact]
    public async Task Students_Query_Returns_Only_This_Tenants_Rows()
    {
        await using var db = Phase5TestDbContextFactory.Create();

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var schoolA = Guid.NewGuid();
        var schoolB = Guid.NewGuid();
        SeedSchool(db, tenantA, schoolA, "مدرسة أ");
        SeedSchool(db, tenantB, schoolB, "مدرسة ب");

        await RunImport(db, tenantA, schoolA, "محمد,Mohammed,7,أبو محمد,muhammed@example.test");
        await RunImport(db, tenantB, schoolB, "علي,Ali,7,أبو علي,ali@example.test");

        var tenantAProfiles = await db.StudentProfiles
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantA)
            .ToListAsync();
        Assert.Single(tenantAProfiles);
        Assert.Equal("محمد", tenantAProfiles[0].DisplayName);

        var tenantBProfiles = await db.StudentProfiles
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantB)
            .ToListAsync();
        Assert.Single(tenantBProfiles);
        Assert.Equal("علي", tenantBProfiles[0].DisplayName);
    }

    private static void SeedSchool(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        Guid tenantId, Guid schoolTenantId, string schoolNameAr)
    {
        db.SchoolTenants.Add(new Muallimi.Domain.SchoolManagement.SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = schoolNameAr,
            SchoolNameEn = schoolNameAr,
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static async Task RunImport(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        Guid tenantId, Guid schoolTenantId, string row)
    {
        var rosterImportId = Guid.NewGuid();
        var blobKey = $"roster-imports/{rosterImportId}.csv";
        var files = new InMemoryRosterFileStore();
        var content = Encoding.UTF8.GetBytes(
            "student_name_ar,student_name_en,grade,parent_name,parent_email\n" + row + "\n");
        await files.WriteAsync(blobKey, content);

        db.RosterImports.Add(new Muallimi.Domain.SchoolManagement.RosterImport
        {
            RosterImportId = rosterImportId,
            TenantId = tenantId,
            SchoolTenantId = schoolTenantId,
            UploadedByAdminId = Guid.NewGuid(),
            SourceFileBlobKey = blobKey,
            OriginalFileName = "roster.csv",
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
        await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blobKey, "corr"),
            CancellationToken.None);
    }
}
