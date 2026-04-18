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

namespace Muallimi.MainBackend.Tests.Integration.SchoolManagement.RosterImport;

/// <summary>
/// T051 (US2) — Integration test for re-import idempotency.
///
/// The same roster file uploaded twice MUST NOT produce duplicate
/// students — the second import collapses all rows onto the existing
/// <see cref="Muallimi.Domain.StudentExperience.StudentProfile"/>
/// records via the deterministic id derived in
/// <see cref="StudentProfileLinker"/>. A partial re-import adding a
/// single new row must create exactly one new student.
/// </summary>
public class ReimportIdempotencyTests
{
    [Fact]
    public async Task Second_Import_Does_Not_Duplicate_Students()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId);

        var files = new InMemoryRosterFileStore();
        var content = Encoding.UTF8.GetBytes(
            "student_name_ar,student_name_en,grade,parent_name,parent_email\n" +
            "محمد,Mohammed,7,أبو محمد,m@example.test\n" +
            "علي,Ali,7,أبو علي,a@example.test\n");

        var outcome1 = await RunImport(db, tenantId, schoolTenantId, files, content, "outcome-1");
        Assert.Equal(2, outcome1.SuccessCount);

        var outcome2 = await RunImport(db, tenantId, schoolTenantId, files, content, "outcome-2");
        // Re-import: rows are still treated as SUCCESS because they
        // collapse onto the existing students (no new rows, but the
        // linker reports the outcome as StudentExisted=true).
        Assert.Equal(2, outcome2.SuccessCount);

        var studentCount = await db.StudentProfiles
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .CountAsync();
        Assert.Equal(2, studentCount);

        var childLinkCount = await db.ChildLinks
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId)
            .CountAsync();
        // Two (student, parent) links — one per student — even after
        // re-import.
        Assert.Equal(2, childLinkCount);
    }

    [Fact]
    public async Task Adding_New_Row_On_Reimport_Creates_One_New_Student()
    {
        await using var db = Phase5TestDbContextFactory.Create();
        var tenantId = Guid.NewGuid();
        var schoolTenantId = Guid.NewGuid();
        SeedSchool(db, tenantId, schoolTenantId);

        var files = new InMemoryRosterFileStore();
        var first = Encoding.UTF8.GetBytes(
            "student_name_ar,student_name_en,grade,parent_name,parent_email\n" +
            "محمد,Mohammed,7,أبو محمد,m@example.test\n");
        await RunImport(db, tenantId, schoolTenantId, files, first, "outcome-first");

        var second = Encoding.UTF8.GetBytes(
            "student_name_ar,student_name_en,grade,parent_name,parent_email\n" +
            "محمد,Mohammed,7,أبو محمد,m@example.test\n" +
            "عائشة,Aisha,8,أم عائشة,aisha@example.test\n");
        await RunImport(db, tenantId, schoolTenantId, files, second, "outcome-second");

        var studentCount = await db.StudentProfiles
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .CountAsync();
        Assert.Equal(2, studentCount);
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
        return await worker.ProcessAsync(
            new RosterImportProcessInput(tenantId, schoolTenantId, rosterImportId, blobKey, correlationId),
            CancellationToken.None);
    }

    private static void SeedSchool(
        Muallimi.Infrastructure.Persistence.MuallimiDbContext db,
        Guid tenantId, Guid schoolTenantId)
    {
        db.SchoolTenants.Add(new Muallimi.Domain.SchoolManagement.SchoolTenant
        {
            SchoolTenantId = schoolTenantId,
            TenantId = tenantId,
            SchoolNameAr = "مدرسة",
            SchoolNameEn = "School",
            CurriculumType = "moe",
            GradeRangeStart = 1,
            GradeRangeEnd = 12,
            CreatedByOperatorId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }
}
